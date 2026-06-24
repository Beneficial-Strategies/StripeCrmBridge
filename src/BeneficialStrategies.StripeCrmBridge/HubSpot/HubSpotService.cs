using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace BeneficialStrategies.StripeCrmBridge.HubSpot;

/// <summary>
/// HubSpot CRM integration via the Contacts v3 batch-upsert API.
/// Contacts are keyed on email — existing contacts are updated, new emails create contacts.
/// All custom properties must be defined in HubSpot before the first sync.
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
        var payload = new
        {
            inputs = contacts.Select(c => new
            {
                idProperty = "email",
                id = c.Email,
                properties = BuildProperties(c)
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
            return;
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("HubSpot batch upsert {StatusCode}: {Body}", (int)response.StatusCode, body);
        }
        else
        {
            _logger.LogDebug("HubSpot: {Count} contact(s) synced", contacts.Length);
        }
    }

    private static Dictionary<string, string?> BuildProperties(ContactSyncData c)
    {
        var props = new Dictionary<string, string?>
        {
            ["email"] = c.Email,
            ["firstname"] = c.DisplayName,
            ["stripe_customer_id"] = c.StripeCustomerId,
        };

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
