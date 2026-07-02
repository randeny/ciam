using System.Globalization;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Xml;

namespace OpenIdServer.Saml;

public sealed record SamlClaim(string Type, string Value);

public sealed class SamlValidationResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? Issuer { get; init; }
    public string? Subject { get; init; }
    public DateTimeOffset? NotBefore { get; init; }
    public DateTimeOffset? NotOnOrAfter { get; init; }
    public string? Audience { get; init; }
    public bool SignatureValidated { get; init; }
    public IReadOnlyList<SamlClaim> Claims { get; init; } = Array.Empty<SamlClaim>();
    public string RawXml { get; init; } = string.Empty;
}

/// <summary>
/// Decodes and validates a SAML 2.0 Response received on the ACS endpoint via the
/// HTTP-POST binding (IdP-initiated SSO).
/// </summary>
public sealed class SamlResponseProcessor
{
    private const string AssertionNs = "urn:oasis:names:tc:SAML:2.0:assertion";
    private const string ProtocolNs = "urn:oasis:names:tc:SAML:2.0:protocol";
    private const string DsNs = "http://www.w3.org/2000/09/xmldsig#";

    private readonly SamlOptions _options;
    private readonly IdpMetadata _metadata;
    private readonly IReplayCache _replayCache;

    public SamlResponseProcessor(SamlOptions options, IdpMetadata metadata, IReplayCache replayCache)
    {
        _options = options;
        _metadata = metadata;
        _replayCache = replayCache;
    }

    /// <summary>
    /// Processes the base64-encoded SAMLResponse form field value.
    /// </summary>
    public SamlValidationResult Process(string samlResponseBase64)
    {
        string xml;
        try
        {
            xml = Encoding.UTF8.GetString(Convert.FromBase64String(samlResponseBase64));
        }
        catch (FormatException)
        {
            return new SamlValidationResult { Success = false, Error = "SAMLResponse is not valid base64." };
        }

        var doc = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        try
        {
            doc.LoadXml(xml);
        }
        catch (XmlException ex)
        {
            return new SamlValidationResult { Success = false, Error = $"SAMLResponse is not valid XML: {ex.Message}", RawXml = xml };
        }

        var nsmgr = new XmlNamespaceManager(doc.NameTable);
        nsmgr.AddNamespace("samlp", ProtocolNs);
        nsmgr.AddNamespace("saml", AssertionNs);
        nsmgr.AddNamespace("ds", DsNs);

        // Status check.
        var statusCode = doc.SelectSingleNode("//samlp:Status/samlp:StatusCode", nsmgr) as XmlElement;
        var statusValue = statusCode?.GetAttribute("Value");
        if (!string.IsNullOrEmpty(statusValue) &&
            !statusValue.EndsWith(":Success", StringComparison.Ordinal))
        {
            return new SamlValidationResult { Success = false, Error = $"IdP returned a non-success status: {statusValue}", RawXml = xml };
        }

        var assertion = doc.SelectSingleNode("//saml:Assertion", nsmgr) as XmlElement;
        if (assertion is null)
        {
            return new SamlValidationResult { Success = false, Error = "No <saml:Assertion> found in the SAML response.", RawXml = xml };
        }

        // Signature validation (response- or assertion-level).
        bool signatureValidated = false;
        if (_options.ValidateSignature)
        {
            var error = ValidateSignature(doc, assertion, nsmgr, out signatureValidated);
            if (error is not null)
            {
                return new SamlValidationResult { Success = false, Error = error, RawXml = xml };
            }
        }

        var issuer = (assertion.SelectSingleNode("saml:Issuer", nsmgr)
            ?? doc.SelectSingleNode("//samlp:Response/saml:Issuer", nsmgr))?.InnerText.Trim();

        var subject = assertion.SelectSingleNode("saml:Subject/saml:NameID", nsmgr)?.InnerText.Trim();

        // Conditions: audience + lifetime.
        var conditions = assertion.SelectSingleNode("saml:Conditions", nsmgr) as XmlElement;
        DateTimeOffset? notBefore = ParseSamlTime(conditions?.GetAttribute("NotBefore"));
        DateTimeOffset? notOnOrAfter = ParseSamlTime(conditions?.GetAttribute("NotOnOrAfter"));
        var audience = assertion.SelectSingleNode(
            "saml:Conditions/saml:AudienceRestriction/saml:Audience", nsmgr)?.InnerText.Trim();

        if (_options.ValidateLifetime)
        {
            var skew = TimeSpan.FromSeconds(_options.ClockSkewSeconds);
            var now = DateTimeOffset.UtcNow;
            if (notBefore.HasValue && now + skew < notBefore.Value)
            {
                return new SamlValidationResult { Success = false, Error = "Assertion is not yet valid (NotBefore in the future).", RawXml = xml };
            }
            if (notOnOrAfter.HasValue && now - skew >= notOnOrAfter.Value)
            {
                return new SamlValidationResult { Success = false, Error = "Assertion has expired (past NotOnOrAfter).", RawXml = xml };
            }
        }

        if (_options.ValidateAudience)
        {
            var expected = _options.EffectiveAudience;
            if (!string.IsNullOrWhiteSpace(expected) &&
                !string.Equals(audience, expected, StringComparison.Ordinal))
            {
                return new SamlValidationResult
                {
                    Success = false,
                    Error = $"Audience mismatch. Expected '{expected}' but assertion contained '{audience}'.",
                    RawXml = xml,
                };
            }
        }

        // Replay protection: reject an assertion ID that has already been consumed.
        if (_options.DetectReplay)
        {
            var assertionId = assertion.GetAttribute("ID");
            var expiresAt = notOnOrAfter ?? DateTimeOffset.UtcNow.AddMinutes(10);
            if (!_replayCache.TryRegister(assertionId, expiresAt))
            {
                return new SamlValidationResult
                {
                    Success = false,
                    Error = "This assertion has already been processed (possible replay attack).",
                    RawXml = xml,
                };
            }
        }

        var claims = ExtractClaims(assertion, nsmgr, subject);

        return new SamlValidationResult
        {
            Success = true,
            Issuer = issuer,
            Subject = subject,
            NotBefore = notBefore,
            NotOnOrAfter = notOnOrAfter,
            Audience = audience,
            SignatureValidated = signatureValidated,
            Claims = claims,
            RawXml = xml,
        };
    }

    private string? ValidateSignature(XmlDocument doc, XmlElement assertion, XmlNamespaceManager nsmgr, out bool validated)
    {
        validated = false;

        if (_metadata.SigningCertificates.Count == 0)
        {
            return "Signature validation is enabled but no signing certificates were found in the federation metadata.";
        }

        // Prefer the assertion signature; fall back to the response signature.
        var signatureElement =
            assertion.SelectSingleNode("ds:Signature", nsmgr) as XmlElement ??
            doc.SelectSingleNode("/samlp:Response/ds:Signature", nsmgr) as XmlElement;

        if (signatureElement is null)
        {
            return "Signature validation is enabled but the SAML response is not signed.";
        }

        var signedElement = signatureElement.ParentNode as XmlElement ?? assertion;

        var signedXml = new SignedXml(signedElement);
        signedXml.LoadXml(signatureElement);

        // Ensure the signature reference actually covers the signed element (prevents wrapping attacks).
        if (!ReferenceCoversElement(signedXml, signedElement))
        {
            return "The XML signature does not reference the expected SAML element.";
        }

        foreach (var cert in _metadata.SigningCertificates)
        {
            if (signedXml.CheckSignature(cert, verifySignatureOnly: true))
            {
                validated = true;
                return null;
            }
        }

        return "The SAML response signature could not be verified against any signing certificate in the metadata.";
    }

    private static bool ReferenceCoversElement(SignedXml signedXml, XmlElement signedElement)
    {
        if (signedXml.SignedInfo?.References is not { Count: > 0 })
        {
            return false;
        }

        var reference = (Reference)signedXml.SignedInfo.References[0]!;
        var uri = reference.Uri;

        if (string.IsNullOrEmpty(uri))
        {
            // Empty URI means the whole document is signed.
            return true;
        }

        if (!uri.StartsWith('#'))
        {
            return false;
        }

        var referencedId = uri[1..];
        var id = signedElement.GetAttribute("ID");
        return string.Equals(referencedId, id, StringComparison.Ordinal);
    }

    private static List<SamlClaim> ExtractClaims(XmlElement assertion, XmlNamespaceManager nsmgr, string? subject)
    {
        var claims = new List<SamlClaim>();

        if (!string.IsNullOrEmpty(subject))
        {
            claims.Add(new SamlClaim("NameID (Subject)", subject));
        }

        var attributes = assertion.SelectNodes("saml:AttributeStatement/saml:Attribute", nsmgr);
        if (attributes is not null)
        {
            foreach (XmlElement attribute in attributes)
            {
                var name = attribute.GetAttribute("Name");
                if (string.IsNullOrEmpty(name))
                {
                    name = attribute.GetAttribute("FriendlyName");
                }

                var valueNodes = attribute.SelectNodes("saml:AttributeValue", nsmgr);
                if (valueNodes is null || valueNodes.Count == 0)
                {
                    claims.Add(new SamlClaim(name, string.Empty));
                    continue;
                }

                foreach (XmlElement value in valueNodes)
                {
                    claims.Add(new SamlClaim(name, value.InnerText.Trim()));
                }
            }
        }

        // AuthnContext / authentication method, if present.
        var authnContext = assertion.SelectSingleNode(
            "saml:AuthnStatement/saml:AuthnContext/saml:AuthnContextClassRef", nsmgr)?.InnerText.Trim();
        if (!string.IsNullOrEmpty(authnContext))
        {
            claims.Add(new SamlClaim("AuthnContextClassRef", authnContext));
        }

        return claims;
    }

    private static DateTimeOffset? ParseSamlTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dto))
        {
            return dto;
        }

        return null;
    }
}
