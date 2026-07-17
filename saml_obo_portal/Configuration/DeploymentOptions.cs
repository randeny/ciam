namespace OpenIdServer.Configuration;

/// <summary>
/// CORS configuration loaded from the "Cors" section of appsettings.json.
/// </summary>
public sealed class CorsOptions
{
    public const string SectionName = "Cors";

    public string[] AllowedOrigins { get; set; } = [];
}

/// <summary>
/// Configuration for the native-auth SPA backend and the On-Behalf-Of (OBO)
/// exchange that produces a SAML2 assertion. Loaded from the "PortalLogon" section.
/// </summary>
public sealed class PortalLogonOptions
{
    public const string SectionName = "PortalLogon";

    /// <summary>Tenant subdomain used to reach the native-auth host (e.g. "contoso" -> contoso.ciamlogin.com).</summary>
    public string TenantSubdomain { get; set; } = string.Empty;

    /// <summary>Host used to reach the native-auth endpoints (e.g. a "login.contoso.com" custom domain).</summary>
    public string NativeAuthHost { get; set; } = string.Empty;

    public string TenantId { get; set; } = string.Empty;

    /// <summary>Confidential client used for the On-Behalf-Of exchange to a SAML2 token.</summary>
    public string OboClientId { get; set; } = string.Empty;

    /// <summary>Secret for <see cref="OboClientId"/>. Supplied via App Service setting PortalLogon__OboClientSecret (never committed).</summary>
    public string OboClientSecret { get; set; } = string.Empty;

    public string OboScope { get; set; } = string.Empty;

    /// <summary>Token endpoint used for the OBO exchange (tenant custom domain).</summary>
    public string OboTokenUrl { get; set; } = string.Empty;

    /// <summary>SAML SP Assertion Consumer Service URL that receives the auto-posted SAMLResponse.</summary>
    public string AcsUrl { get; set; } = string.Empty;
}
