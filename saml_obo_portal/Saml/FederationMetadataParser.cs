using System.Security.Cryptography.X509Certificates;
using System.Xml;

namespace OpenIdServer.Saml;

/// <summary>
/// Result of parsing IdP federation metadata.
/// </summary>
public sealed class IdpMetadata
{
    public string EntityId { get; init; } = string.Empty;
    public string? SingleSignOnServiceUrl { get; init; }
    public IReadOnlyList<X509Certificate2> SigningCertificates { get; init; } = Array.Empty<X509Certificate2>();
}

/// <summary>
/// Parses WS-Federation / SAML 2.0 federation metadata documents to extract the
/// IdP entity id, SSO service endpoint and the X509 signing certificates.
/// </summary>
public static class FederationMetadataParser
{
    private const string MdNs = "urn:oasis:names:tc:SAML:2.0:metadata";
    private const string DsNs = "http://www.w3.org/2000/09/xmldsig#";

    public static async Task<IdpMetadata> LoadAsync(SamlOptions options, CancellationToken ct = default)
    {
        var xml = await ResolveMetadataXmlAsync(options, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(xml))
        {
            throw new InvalidOperationException(
                "No federation metadata available. Set MetadataUrl, MetadataFilePath or MetadataXml in the Saml configuration section.");
        }

        return Parse(xml);
    }

    public static IdpMetadata Parse(string metadataXml)
    {
        var doc = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        doc.LoadXml(metadataXml);

        var nsmgr = new XmlNamespaceManager(doc.NameTable);
        nsmgr.AddNamespace("md", MdNs);
        nsmgr.AddNamespace("ds", DsNs);

        var entityDescriptor = doc.SelectSingleNode("//md:EntityDescriptor", nsmgr) as XmlElement
            ?? throw new InvalidOperationException("Federation metadata is missing an EntityDescriptor element.");

        var entityId = entityDescriptor.GetAttribute("entityID");

        var idpSsoDescriptor = entityDescriptor.SelectSingleNode("md:IDPSSODescriptor", nsmgr) as XmlElement;

        // Collect signing certificates (use="signing" or unspecified use).
        var certs = new List<X509Certificate2>();
        var keyDescriptors = (idpSsoDescriptor ?? entityDescriptor)
            .SelectNodes(".//md:KeyDescriptor", nsmgr);

        if (keyDescriptors is not null)
        {
            foreach (XmlElement kd in keyDescriptors)
            {
                var use = kd.GetAttribute("use");
                if (!string.IsNullOrEmpty(use) &&
                    !use.Equals("signing", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var certNodes = kd.SelectNodes(".//ds:X509Certificate", nsmgr);
                if (certNodes is null)
                {
                    continue;
                }

                foreach (XmlElement certNode in certNodes)
                {
                    var raw = certNode.InnerText.Trim()
                        .Replace("\r", string.Empty)
                        .Replace("\n", string.Empty)
                        .Replace(" ", string.Empty);
                    if (raw.Length == 0)
                    {
                        continue;
                    }

                    try
                    {
                        certs.Add(X509CertificateLoader.LoadCertificate(Convert.FromBase64String(raw)));
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException("Failed to parse an X509 signing certificate from metadata.", ex);
                    }
                }
            }
        }

        string? ssoUrl = null;
        var ssoNode = idpSsoDescriptor?.SelectSingleNode(
            "md:SingleSignOnService[@Binding='urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST']", nsmgr) as XmlElement
            ?? idpSsoDescriptor?.SelectSingleNode("md:SingleSignOnService", nsmgr) as XmlElement;
        if (ssoNode is not null)
        {
            ssoUrl = ssoNode.GetAttribute("Location");
        }

        return new IdpMetadata
        {
            EntityId = entityId,
            SingleSignOnServiceUrl = ssoUrl,
            SigningCertificates = certs,
        };
    }

    private static async Task<string> ResolveMetadataXmlAsync(SamlOptions options, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(options.MetadataXml))
        {
            return options.MetadataXml;
        }

        if (!string.IsNullOrWhiteSpace(options.MetadataUrl))
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            return await http.GetStringAsync(options.MetadataUrl, ct).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(options.MetadataFilePath))
        {
            var path = options.MetadataFilePath;
            if (!Path.IsPathRooted(path))
            {
                path = Path.Combine(AppContext.BaseDirectory, path);
            }

            if (File.Exists(path))
            {
                return await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            }
        }

        return string.Empty;
    }
}
