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
/// also be defined before the first sync.
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
        var failed = await PostBatchAsync(contacts, withEmail: true, ct);
        if (failed.Length == 0) return;

        // Retry contacts whose email conflicted with an existing contact, this time
        // omitting the email field so the rest of the properties (tier, status, etc.)
        // still land. The existing email on the HubSpot contact is left unchanged.
        _logger.LogWarning(
            "HubSpot email conflict on {Count} contact(s) — retrying without email field: {Ids}",
            failed.Length,
            string.Join(", ", failed.Select(c => c.StripeCustomerId)));

        await PostBatchAsync(failed, withEmail: false, ct);
    }

    private async Task<ContactSyncData[]> PostBatchAsync(ContactSyncData[] contacts, bool withEmail, CancellationToken ct)
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
            return [];
        }

        if (response.IsSuccessStatusCode)
        {
            _logger.LogDebug("HubSpot: {Count} contact(s) synced (withEmail={WithEmail})", contacts.Length, withEmail);
            return [];
        }

        var body = await response.Content.ReadAsStringAsync(ct);

        // A VALIDATION_ERROR on the email property means the billing email is already
        // assigned to a different HubSpot contact. Return these contacts for a retry
        // without the email field so the remaining properties still sync.
        // HubSpot error formats vary: batch errors say "propertyName=email" while
        // single-property errors say "\"email\"" — match either form.
        if (withEmail && body.Contains("VALIDATION_ERROR") &&
            (body.Contains("propertyName=email") || body.Contains("\"email\"")))
            return contacts;

        _logger.LogWarning("HubSpot batch upsert {StatusCode}: {Body}", (int)response.StatusCode, body);
        return [];
    }

    private static Dictionary<string, string?> BuildProperties(ContactSyncData c, bool includeEmail = true)
    {
        var props = new Dictionary<string, string?>();

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
