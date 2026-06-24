namespace BeneficialStrategies.StripeCrmBridge.HubSpot;

/// <summary>
/// Customer data sent to HubSpot. Built from Stripe event payloads — no database required.
/// <para>
/// Contacts are keyed on <see cref="StripeCustomerId"/>, which must be configured as a
/// unique identifier in HubSpot. Fields left null are omitted from the upsert so existing
/// HubSpot values are preserved rather than overwritten with nulls.
/// </para>
/// <para>
/// <see cref="BillingEmail"/> is the Stripe customer email (correspondence address).
/// <see cref="LoginIdentity"/> is left null here — the nightly sweep in the main
/// application populates it from the OAuth login identity in the application database.
/// </para>
/// </summary>
public record ContactSyncData(
    string StripeCustomerId,
    string? BillingEmail,
    string? LoginIdentity,
    string? DisplayName,
    string? InternalUserId,
    string? SubscriptionTier,
    string? SubscriptionStatus,
    DateTime? TrialStartDate,
    DateTime? TrialEndDate,
    string? CardLastFour,
    int? CardExpMonth,
    int? CardExpYear);
