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

    /// <summary>
    /// Which deployment environment this instance is running as — written to every synced
    /// Contact's <c>environment</c> property (values: <c>"staging"</c> / <c>"production"</c>,
    /// lowercased on write). Staging and production write to the same HubSpot portal as the
    /// main MCP server (see that repo's docs/CRM.md "Environments") — this property is what
    /// distinguishes a real customer record from a test one there.
    /// </summary>
    public string Environment { get; set; } = "production";
}
