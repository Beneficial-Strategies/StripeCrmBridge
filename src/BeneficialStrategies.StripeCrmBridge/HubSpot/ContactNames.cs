namespace BeneficialStrategies.StripeCrmBridge.HubSpot;

/// <summary>
/// The names of a HubSpot contact as the sync writes them. The display name (the Stripe customer's name) is only a
/// first guess: the sync writes it into First Name and Last Name when the contact has neither, and never over a value
/// a person set. Mirrors the main app's <c>ContactNames</c>; the two do not share code, so keep them in step by hand.
/// </summary>
public static class ContactNames
{
    /// <summary>
    /// Splits a display name: the last word is the Last Name and everything before it the First Name; a single word
    /// is a First Name only. Returns null when there is nothing to write.
    /// </summary>
    public static (string First, string? Last)? Split(string? displayName)
    {
        var words = displayName?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words is null || words.Length == 0) return null;
        return words.Length == 1
            ? (words[0], null)
            : (string.Join(' ', words[..^1]), words[^1]);
    }

    /// <summary>
    /// Whether the sync may write the names: HubSpot could be asked, and the contact either does not exist yet or has
    /// both names empty. When HubSpot cannot be asked the names are left alone; the next sync tries again.
    /// </summary>
    public static bool MayWrite(bool hubSpotAnswered, bool contactExists, string? firstName, string? lastName) =>
        hubSpotAnswered && (!contactExists || (string.IsNullOrWhiteSpace(firstName) && string.IsNullOrWhiteSpace(lastName)));
}
