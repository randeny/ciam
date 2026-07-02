namespace OpenIdServer.Saml;

/// <summary>
/// Strongly-typed SAML configuration loaded from the "Saml" section of appsettings.json.
/// </summary>
public sealed class SamlOptions
{
    public const string SectionName = "Saml";

    public string ServiceProviderEntityId { get; set; } = string.Empty;
    public string AssertionConsumerServiceUrl { get; set; } = string.Empty;
    public string ExpectedAudience { get; set; } = string.Empty;

    public string MetadataUrl { get; set; } = string.Empty;
    public string MetadataFilePath { get; set; } = string.Empty;
    public string MetadataXml { get; set; } = string.Empty;

    public bool ValidateSignature { get; set; } = true;
    public bool ValidateAudience { get; set; } = true;
    public bool ValidateLifetime { get; set; } = true;
    public bool DetectReplay { get; set; } = true;
    public int ClockSkewSeconds { get; set; } = 300;

    public string EffectiveAudience =>
        string.IsNullOrWhiteSpace(ExpectedAudience) ? ServiceProviderEntityId : ExpectedAudience;
}
