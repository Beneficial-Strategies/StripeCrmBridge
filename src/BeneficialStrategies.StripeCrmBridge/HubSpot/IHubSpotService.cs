namespace BeneficialStrategies.StripeCrmBridge.HubSpot;

/// <summary>
/// Pushes customer contact records to HubSpot CRM. All methods silently no-op
/// when HubSpot is disabled so callers need no guard checks.
/// </summary>
public interface IHubSpotService
{
    /// <summary>Whether HubSpot integration is configured and enabled.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Upserts a single contact, keyed on email address.
    /// </summary>
    /// <exception cref="HubSpotSyncException">HubSpot was unreachable or refused the sync, so the contact is not up to date. A webhook
    /// must not answer Stripe 200 for it.</exception>
    Task SyncContactAsync(ContactSyncData contact, CancellationToken ct = default);

    /// <summary>Upserts a batch of contacts (100 per API call).</summary>
    Task BulkSyncContactsAsync(IEnumerable<ContactSyncData> contacts, CancellationToken ct = default);
}
