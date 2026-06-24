namespace BeneficialStrategies.StripeCrmBridge.HubSpot;

/// <summary>
/// Configuration for the HubSpot CRM integration.
/// Set <c>HubSpot__AccessToken</c> via environment variable. Never commit a token.
/// </summary>
public class HubSpotSettings
{
    public const string SectionName = "HubSpot";

    /// <summary>HubSpot Private App access token (pat-na1-xxx).</summary>
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>When false all sync calls are silent no-ops.</summary>
    public bool Enabled { get; set; }

    /// <summary>HubSpot API base URL.</summary>
    public string BaseUrl { get; set; } = "https://api.hubapi.com";
}
