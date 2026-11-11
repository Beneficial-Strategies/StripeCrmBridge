namespace BeneficialStrategies.StripeCrmBridge.HubSpot;

/// <summary>
/// A webhook's sync of one contact to HubSpot did not land. The webhook receiver answers Stripe with an error for it, never 200,
/// so that Stripe delivers the event again (live mode: for up to three days) and the failure shows in its dashboard.
/// </summary>
/// <remarks>
/// <see cref="Retryable"/> is true when delivering the same event again can succeed without anyone changing anything: HubSpot was
/// unreachable or timed out, or answered 408, 429 or a 5xx. It is false when HubSpot refused the request (400, 401, 403, 404 and the like):
/// a person has to look, and a redelivery after the fix will land it. The sweep that syncs many contacts at once does not use this:
/// it logs a failure and carries on.
/// </remarks>
public sealed class HubSpotSyncException(string message, bool retryable, int? statusCode, TimeSpan? retryAfter = null) : Exception(message)
{
    /// <summary>Whether delivering the event again can succeed as it is.</summary>
    public bool Retryable { get; } = retryable;

    /// <summary>The HTTP status HubSpot answered with; null when it could not be reached.</summary>
    public int? StatusCode { get; } = statusCode;

    /// <summary>How long HubSpot asked callers to wait (its <c>Retry-After</c> on a 429), when it said.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
