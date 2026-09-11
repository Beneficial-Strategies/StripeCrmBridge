using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace BeneficialStrategies.StripeCrmBridge.HubSpot;

/// <summary>
/// HubSpot CRM integration via the Contacts v3 batch-upsert API.
/// <para>
/// Contacts are keyed on <c>stripe_customer_id</c> — that property must be configured
/// as a unique identifier in HubSpot (Settings → Properties → Contacts →
/// stripe_customer_id → check "Used as a unique ID"). All other custom properties must
/// also be defined before the first sync, including <c>environment</c> — see
/// <see cref="HubSpotSettings.Environment"/>.
/// </para>
/// <para>
/// Staging and production share one HubSpot portal, and HubSpot enforces email uniqueness
/// portal-wide — a sync can collide with a <em>different</em> existing contact that already
/// owns the address (e.g. a real production customer's email reused in a staging test; see
/// the main repo's docs/CRM.md, "Email address separation"). <see cref="TryResolveEmailCollisionAsync"/>
/// recovers from this: merges a bare-lead collision in automatically, refuses and logs a
/// real-account collision. Mirrors the main app's <c>HubSpotService.TryResolveEmailCollisionAsync</c>
/// exactly — the two don't share code, so keep them in sync by hand if either changes.
/// </para>
/// </summary>
public sealed class HubSpotService : IHubSpotService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly HubSpotSettings _settings;
    private readonly ILogger<HubSpotService> _logger;

    /// <inheritdoc/>
    public bool IsEnabled => _settings.Enabled && !string.IsNullOrEmpty(_settings.AccessToken);

    public HubSpotService(
        IHttpClientFactory factory,
        IOptions<HubSpotSettings> settings,
        ILogger<HubSpotService> logger)
    {
        _settings = settings.Value;
        _logger = logger;
        _http = factory.CreateClient();
        _http.BaseAddress = new Uri(_settings.BaseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(10);
        if (!string.IsNullOrEmpty(_settings.AccessToken))
        {
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _settings.AccessToken);
        }
    }

    /// <inheritdoc/>
    public async Task SyncContactAsync(ContactSyncData contact, CancellationToken ct = default)
    {
        if (!IsEnabled) return;
        await BulkSyncContactsAsync([contact], ct);
    }

    /// <inheritdoc/>
    public async Task BulkSyncContactsAsync(IEnumerable<ContactSyncData> contacts, CancellationToken ct = default)
    {
        if (!IsEnabled) return;
        var all = contacts.ToList();
        if (all.Count == 0) return;
        foreach (var batch in all.Chunk(100))
            await UpsertBatchAsync(batch, ct);
    }

    private async Task UpsertBatchAsync(ContactSyncData[] contacts, CancellationToken ct)
    {
        if (contacts.Length == 1)
        {
            // The only caller today (SyncContactAsync) always passes exactly one contact —
            // route through UpsertOneAsync so there's exactly one place that knows how to
            // recover from an email collision (merge or refuse), not just drop the field.
            await UpsertOneAsync(contacts[0], ct);
            return;
        }

        var (failed, _) = await PostBatchAsync(contacts, withEmail: true, ct);
        if (failed.Length == 0) return;

        // Ambiguous which contact(s) in a multi-contact batch caused the email-validation
        // error — retry one at a time so UpsertOneAsync can attempt collision recovery
        // against a single, unambiguous contact.
        _logger.LogWarning(
            "HubSpot email conflict on {Count} contact(s) — retrying individually: {Ids}",
            failed.Length,
            string.Join(", ", failed.Select(c => c.StripeCustomerId)));

        foreach (var contact in failed)
            await UpsertOneAsync(contact, ct);
    }

    private async Task UpsertOneAsync(ContactSyncData contact, CancellationToken ct)
    {
        var (failed, errorBody) = await PostBatchAsync([contact], withEmail: true, ct);
        if (failed.Length == 0) return;

        if (errorBody != null && await TryResolveEmailCollisionAsync(contact, errorBody, ct))
            return;

        // Collision recovery didn't apply (couldn't identify the colliding contact, or it's a
        // real, already billing-linked account) — fall back to syncing everything except
        // email so the rest of the properties (tier, status, etc.) still land. The existing
        // email on the HubSpot contact is left unchanged.
        _logger.LogWarning("HubSpot email conflict for {CustomerId} — syncing without email field.", contact.StripeCustomerId);
        await PostBatchAsync([contact], withEmail: false, ct);
    }

    private async Task<(ContactSyncData[] Failed, string? ErrorBody)> PostBatchAsync(ContactSyncData[] contacts, bool withEmail, CancellationToken ct)
    {
        var payload = new
        {
            inputs = contacts.Select(c => new
            {
                idProperty = "stripe_customer_id",
                id = c.StripeCustomerId,
                properties = BuildProperties(c, includeEmail: withEmail)
            })
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsync("crm/v3/objects/contacts/batch/upsert", content, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HubSpot API unreachable");
            return ([], null);
        }

        if (response.IsSuccessStatusCode)
        {
            _logger.LogDebug("HubSpot: {Count} contact(s) synced (withEmail={WithEmail})", contacts.Length, withEmail);
            return ([], null);
        }

        var body = await response.Content.ReadAsStringAsync(ct);

        // A VALIDATION_ERROR on the email property means the billing email is already
        // assigned to a different HubSpot contact. Return these contacts (plus the raw body,
        // for TryResolveEmailCollisionAsync to parse) for a retry.
        // HubSpot error formats vary: batch errors say "propertyName=email" while
        // single-property errors say "\"email\"" — match either form.
        if (withEmail && LooksLikeEmailCollision(body))
            return (contacts, body);

        _logger.LogWarning("HubSpot batch upsert {StatusCode}: {Body}", (int)response.StatusCode, body);
        return ([], null);
    }

    private static bool LooksLikeEmailCollision(string body) =>
        body.Contains("VALIDATION_ERROR", StringComparison.Ordinal) &&
        (body.Contains("propertyName=email", StringComparison.Ordinal) || body.Contains("\"email\"", StringComparison.Ordinal));

    // ── Email collision recovery ─────────────────────────────────────────────────

    // Matches HubSpot's actual (non-JSON-structured) validation message, e.g.:
    // "Cannot set PropertyValueCoordinates{..., propertyName=email, value=x@y.com} on
    // 550260950729. 511099411174 already has that value." — captures "511099411174", the
    // HubSpot ID of the contact that already owns the email. Not a documented/stable format;
    // every use of it fails soft (falls through to the existing "sync without email"
    // behavior) rather than throwing.
    private static readonly System.Text.RegularExpressions.Regex CollidingContactIdPattern =
        new(@"(\d+)\s+already has that value", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Recovers from HubSpot refusing to set a contact's email because a <em>different</em>
    /// existing contact already has that exact value.
    /// <para>
    /// Only merges when the colliding contact has no <c>stripe_customer_id</c> of its own (a
    /// bare lead — safe to absorb). Refuses (returns false, letting the caller fall back to
    /// syncing without email) when it does: that's a distinct, already billing-linked account,
    /// and silently combining two real customers' billing history is the one outcome this must
    /// never produce.
    /// </para>
    /// </summary>
    /// <returns>
    /// True if the collision was resolved and a retried sync was attempted; false if recovery
    /// didn't apply and the caller should fall back to its own handling.
    /// </returns>
    private async Task<bool> TryResolveEmailCollisionAsync(ContactSyncData contact, string upsertErrorBody, CancellationToken ct)
    {
        var otherContactId = ExtractCollidingContactId(upsertErrorBody);
        if (otherContactId is null) return false;

        var survivorId = await GetContactIdByStripeCustomerIdAsync(contact.StripeCustomerId, ct);
        if (survivorId is null)
        {
            _logger.LogWarning(
                "HubSpot email collision for {CustomerId} — could not resolve the contact's own " +
                "HubSpot ID to attempt a merge.", contact.StripeCustomerId);
            return false;
        }

        var otherStripeCustomerId = await GetStripeCustomerIdOfContactAsync(otherContactId, ct);
        if (!string.IsNullOrEmpty(otherStripeCustomerId))
        {
            // A different, already billing-linked account owns this email — never combine two
            // real customers' history. Often expected, not a bug in itself (e.g. a real
            // production customer's email reused in a staging test) — but always needs a
            // human to look at, since the email property on `survivorId` is now stuck stale.
            _logger.LogWarning(
                "HubSpot email collision refused for {CustomerId} — contact {OtherId} already " +
                "has a different stripe_customer_id ({OtherCustomerId}). Not merging; the email " +
                "property on {SurvivorId} was NOT updated. Needs manual reconciliation if this " +
                "is unexpected.",
                contact.StripeCustomerId, otherContactId, otherStripeCustomerId, survivorId);
            return false;
        }

        // No stripe_customer_id on the colliding contact — a bare lead, safe to absorb.
        var mergePayload = new { primaryObjectId = survivorId, objectIdToMerge = otherContactId };
        var mergeJson = JsonSerializer.Serialize(mergePayload, JsonOptions);
        using var mergeContent = new StringContent(mergeJson, Encoding.UTF8, "application/json");

        HttpResponseMessage mergeResponse;
        try
        {
            mergeResponse = await _http.PostAsync("crm/v3/objects/contacts/merge", mergeContent, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HubSpot email-collision merge unreachable: survivor={SurvivorId} lead={LeadId}",
                survivorId, otherContactId);
            return false;
        }

        if (!mergeResponse.IsSuccessStatusCode)
        {
            var mergeBody = await mergeResponse.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("HubSpot email-collision merge {StatusCode}: survivor={SurvivorId} lead={LeadId} body={Body}",
                (int)mergeResponse.StatusCode, survivorId, otherContactId, mergeBody);
            return false;
        }

        _logger.LogInformation(
            "HubSpot email collision resolved: bare-lead contact {LeadId} merged into " +
            "{SurvivorId} for {CustomerId}. Retrying the property sync.",
            otherContactId, survivorId, contact.StripeCustomerId);

        var (retryFailed, _) = await PostBatchAsync([contact], withEmail: true, ct);
        if (retryFailed.Length > 0)
            _logger.LogWarning("HubSpot retry-after-merge still failed for {CustomerId}.", contact.StripeCustomerId);

        return true;
    }

    private string? ExtractCollidingContactId(string errorBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(errorBody);
            var message = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null;
            if (message is null) return null;
            var match = CollidingContactIdPattern.Match(message);
            return match.Success ? match.Groups[1].Value : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<string?> GetContactIdByStripeCustomerIdAsync(string stripeCustomerId, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(
                $"crm/v3/objects/contacts/{Uri.EscapeDataString(stripeCustomerId)}?idProperty=stripe_customer_id", ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HubSpot lookup by stripe_customer_id unreachable for {CustomerId}", stripeCustomerId);
            return null;
        }

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) return null;

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("id").GetString();
    }

    private async Task<string?> GetStripeCustomerIdOfContactAsync(string hubspotContactId, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync($"crm/v3/objects/contacts/{hubspotContactId}?properties=stripe_customer_id", ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HubSpot contact lookup unreachable for {ContactId}", hubspotContactId);
            return null;
        }

        if (!response.IsSuccessStatusCode) return null;

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var props = doc.RootElement.GetProperty("properties");
        return props.TryGetProperty("stripe_customer_id", out var s) ? s.GetString() : null;
    }

    private Dictionary<string, string?> BuildProperties(ContactSyncData c, bool includeEmail = true)
    {
        var props = new Dictionary<string, string?>
        {
            ["environment"] = _settings.Environment.ToLowerInvariant(),
        };

        // billing_email → HubSpot "email" field (correspondence/outbound address)
        if (includeEmail && c.BillingEmail != null) props["email"] = c.BillingEmail;
        if (c.LoginIdentity != null)       props["login_identity"] = c.LoginIdentity;
        if (c.DisplayName != null)         props["firstname"] = c.DisplayName;
        if (c.InternalUserId != null)      props["internal_customer_id"] = c.InternalUserId;
        if (c.SubscriptionTier != null)    props["subscription_tier"] = c.SubscriptionTier.ToLowerInvariant();
        if (c.SubscriptionStatus != null)  props["subscription_status"] = c.SubscriptionStatus.ToLowerInvariant();
        if (c.TrialStartDate.HasValue)     props["trial_start_date"] = c.TrialStartDate.Value.ToString("yyyy-MM-dd");
        if (c.TrialEndDate.HasValue)       props["trial_end_date"] = c.TrialEndDate.Value.ToString("yyyy-MM-dd");
        if (c.CardExpMonth.HasValue)       props["card_exp_month"] = c.CardExpMonth.Value.ToString();
        if (c.CardExpYear.HasValue)        props["card_exp_year"] = c.CardExpYear.Value.ToString();

        return props;
    }
}
