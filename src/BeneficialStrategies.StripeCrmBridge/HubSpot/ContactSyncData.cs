namespace BeneficialStrategies.StripeCrmBridge.HubSpot;

/// <summary>
/// Customer data sent to HubSpot. Built from Stripe event payloads — no database required.
/// Fields left null are omitted from the HubSpot upsert so existing values are preserved.
/// </summary>
public record ContactSyncData(
    string Email,
    string? DisplayName,
    string StripeCustomerId,
    string? InternalUserId,
    string? SubscriptionTier,
    string? SubscriptionStatus,
    DateTime? TrialStartDate,
    DateTime? TrialEndDate,
    string? CardLastFour,
    int? CardExpMonth,
    int? CardExpYear);
