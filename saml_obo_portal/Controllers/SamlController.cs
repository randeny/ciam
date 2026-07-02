using System.Net;
using System.Text;
using System.Web;
using System.Xml;
using Microsoft.AspNetCore.Mvc;

namespace OpenIdServer.Saml;

/// <summary>
/// SAML 2.0 Service Provider endpoints exposed under <c>/samlapp</c>:
///   GET  /samlapp/acs        — information page for the Assertion Consumer Service.
///   POST /samlapp/acs        — consumes an HTTP-POST SAML Response (IdP-initiated SSO), then
///                              redirects (302) to the claims reflector below.
///   GET  /samlapp/result/{id} — renders the validated assertion's claims (Post-Redirect-Get target).
///   GET  /samlapp/metadata   — publishes this Service Provider's SAML metadata.
/// </summary>
[ApiController]
[Route("samlapp")]
public sealed class SamlController : ControllerBase
{
    private const string MdNs = "urn:oasis:names:tc:SAML:2.0:metadata";

    private readonly SamlOptions _options;
    private readonly IReplayCache _replayCache;
    private readonly ISamlResultStore _resultStore;
    private readonly ILogger<SamlController> _logger;

    public SamlController(SamlOptions options, IReplayCache replayCache, ISamlResultStore resultStore, ILogger<SamlController> logger)
    {
        _options = options;
        _replayCache = replayCache;
        _resultStore = resultStore;
        _logger = logger;
    }

    [HttpGet("acs")]
    public IActionResult AcsInfo() => Html(HttpStatusCode.OK, RenderInfoPage());

    [HttpPost("acs")]
    public async Task<IActionResult> Acs()
    {
        if (!Request.HasFormContentType)
        {
            return Html(HttpStatusCode.BadRequest, RenderError("Expected an application/x-www-form-urlencoded POST containing a SAMLResponse."));
        }

        var form = await Request.ReadFormAsync(HttpContext.RequestAborted);
        var samlResponse = form["SAMLResponse"].ToString();
        if (string.IsNullOrWhiteSpace(samlResponse))
        {
            return Html(HttpStatusCode.BadRequest, RenderError("Missing 'SAMLResponse' form field."));
        }

        IdpMetadata metadata;
        try
        {
            metadata = await FederationMetadataParser.LoadAsync(_options, HttpContext.RequestAborted);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load federation metadata.");
            return Html(HttpStatusCode.InternalServerError, RenderError($"Federation metadata error: {ex.Message}"));
        }

        var processor = new SamlResponseProcessor(_options, metadata, _replayCache);
        var result = processor.Process(samlResponse);

        if (!result.Success)
        {
            _logger.LogWarning("SAML validation failed: {Error}", result.Error);
        }
        else
        {
            _logger.LogInformation("SAML assertion accepted for subject {Subject}", result.Subject);
        }

        // Post-Redirect-Get: stash the validated result and 302 to the claims reflector so the
        // result is shown on a clean URL and the page is refresh-safe (no form re-POST).
        var resultId = _resultStore.Store(result, TimeSpan.FromMinutes(5));
        return Redirect($"{ResolveBasePath()}/result/{resultId}");
    }

    [HttpGet("result/{id}")]
    public IActionResult Result(string id)
    {
        var result = _resultStore.Take(id);
        if (result is null)
        {
            return Html(HttpStatusCode.OK, RenderExpired());
        }

        var status = result.Success ? HttpStatusCode.OK : HttpStatusCode.BadRequest;
        return Html(status, RenderResult(result));
    }

    [HttpGet("metadata")]
    public IActionResult Metadata()
    {
        var xml = BuildSpMetadata();
        return new ContentResult
        {
            Content = xml,
            ContentType = "application/samlmetadata+xml",
            StatusCode = StatusCodes.Status200OK,
        };
    }

    private IActionResult Html(HttpStatusCode status, string html) =>
        new ContentResult { Content = html, ContentType = "text/html; charset=utf-8", StatusCode = (int)status };

    private string ResolveBasePath() => $"{Request.PathBase}/samlapp";

    private string RenderExpired()
    {
        var sb = new StringBuilder();
        AppendHeader(sb, "SAML Assertion");
        sb.Append("<div class=\"banner error\">This result has expired or was already viewed. ")
          .Append("Start a new sign-in from the identity provider to see the claims.</div>");
        AppendFooter(sb);
        return sb.ToString();
    }

    private string BuildSpMetadata()
    {
        var acsUrl = !string.IsNullOrWhiteSpace(_options.AssertionConsumerServiceUrl)
            ? _options.AssertionConsumerServiceUrl
            : _options.ServiceProviderEntityId;

        var settings = new XmlWriterSettings
        {
            Indent = true,
            OmitXmlDeclaration = false,
            Encoding = new UTF8Encoding(false),
        };

        using var sw = new StringWriter();
        using (var writer = XmlWriter.Create(sw, settings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("md", "EntityDescriptor", MdNs);
            writer.WriteAttributeString("entityID", _options.ServiceProviderEntityId);

            writer.WriteStartElement("md", "SPSSODescriptor", MdNs);
            writer.WriteAttributeString("AuthnRequestsSigned", "false");
            writer.WriteAttributeString("WantAssertionsSigned",
                _options.ValidateSignature ? "true" : "false");
            writer.WriteAttributeString("protocolSupportEnumeration",
                "urn:oasis:names:tc:SAML:2.0:protocol");

            writer.WriteStartElement("md", "NameIDFormat", MdNs);
            writer.WriteString("urn:oasis:names:tc:SAML:2.0:nameid-format:persistent");
            writer.WriteEndElement();

            writer.WriteStartElement("md", "AssertionConsumerService", MdNs);
            writer.WriteAttributeString("Binding", "urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST");
            writer.WriteAttributeString("Location", acsUrl);
            writer.WriteAttributeString("index", "0");
            writer.WriteAttributeString("isDefault", "true");
            writer.WriteEndElement();

            writer.WriteEndElement(); // SPSSODescriptor
            writer.WriteEndElement(); // EntityDescriptor
            writer.WriteEndDocument();
        }

        return sw.ToString();
    }

    private string RenderInfoPage()
    {
        var sb = new StringBuilder();
        AppendHeader(sb, "SAML ACS Endpoint");
        sb.Append("<p>This is the Assertion Consumer Service (ACS) endpoint. It accepts an HTTP-POST SAML Response for IdP-initiated SSO.</p>");
        sb.Append("<h2>Service Provider configuration</h2><table>");
        Row(sb, "Entity ID", _options.ServiceProviderEntityId);
        Row(sb, "ACS URL", _options.AssertionConsumerServiceUrl);
        Row(sb, "Audience", _options.EffectiveAudience);
        Row(sb, "Metadata URL", $"{Request.Scheme}://{Request.Host}/samlapp/metadata");
        Row(sb, "Validate signature", _options.ValidateSignature.ToString());
        Row(sb, "Validate audience", _options.ValidateAudience.ToString());
        Row(sb, "Validate lifetime", _options.ValidateLifetime.ToString());
        Row(sb, "Detect replay", _options.DetectReplay.ToString());
        sb.Append("</table>");
        AppendFooter(sb);
        return sb.ToString();
    }

    private static string RenderResult(SamlValidationResult result)
    {
        var sb = new StringBuilder();
        AppendHeader(sb, "SAML Assertion");

        if (!result.Success)
        {
            sb.Append("<div class=\"banner error\">Validation failed: ")
              .Append(HttpUtility.HtmlEncode(result.Error))
              .Append("</div>");
        }
        else
        {
            sb.Append("<div class=\"banner ok\">SAML assertion validated successfully.</div>");
        }

        sb.Append("<h2>Summary</h2><table>");
        Row(sb, "Issuer", result.Issuer);
        Row(sb, "Subject", result.Subject);
        Row(sb, "Audience", result.Audience);
        Row(sb, "Not before", result.NotBefore?.ToString("u"));
        Row(sb, "Not on or after", result.NotOnOrAfter?.ToString("u"));
        Row(sb, "Signature validated", result.SignatureValidated.ToString());
        sb.Append("</table>");

        sb.Append("<h2>Claims</h2>");
        if (result.Claims.Count == 0)
        {
            sb.Append("<p>No claims were found in the assertion.</p>");
        }
        else
        {
            sb.Append("<table><tr><th>Type</th><th>Value</th></tr>");
            foreach (var claim in result.Claims)
            {
                sb.Append("<tr><td><code>")
                  .Append(HttpUtility.HtmlEncode(claim.Type))
                  .Append("</code></td><td>")
                  .Append(HttpUtility.HtmlEncode(claim.Value))
                  .Append("</td></tr>");
            }
            sb.Append("</table>");
        }

        if (!string.IsNullOrEmpty(result.RawXml))
        {
            sb.Append("<h2>Decoded SAML response</h2><details><summary>Show XML</summary><pre>")
              .Append(HttpUtility.HtmlEncode(result.RawXml))
              .Append("</pre></details>");
        }

        AppendFooter(sb);
        return sb.ToString();
    }

    private static string RenderError(string message)
    {
        var sb = new StringBuilder();
        AppendHeader(sb, "SAML ACS Endpoint");
        sb.Append("<div class=\"banner error\">").Append(HttpUtility.HtmlEncode(message)).Append("</div>");
        AppendFooter(sb);
        return sb.ToString();
    }

    private static void Row(StringBuilder sb, string label, string? value) =>
        sb.Append("<tr><th>").Append(HttpUtility.HtmlEncode(label)).Append("</th><td>")
          .Append(HttpUtility.HtmlEncode(value ?? string.Empty)).Append("</td></tr>");

    private static void AppendHeader(StringBuilder sb, string title)
    {
        sb.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\">")
          .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
          .Append("<title>").Append(HttpUtility.HtmlEncode(title)).Append("</title><style>")
          .Append("body{font-family:Segoe UI,Arial,sans-serif;margin:2rem;color:#222;max-width:960px}")
          .Append("h1{font-size:1.5rem}h2{font-size:1.1rem;margin-top:1.5rem;border-bottom:1px solid #ddd;padding-bottom:.25rem}")
          .Append("table{border-collapse:collapse;width:100%;margin:.5rem 0}")
          .Append("th,td{border:1px solid #ddd;padding:.4rem .6rem;text-align:left;vertical-align:top;font-size:.9rem}")
          .Append("th{background:#f5f5f5;width:220px}")
          .Append("pre{background:#1e1e1e;color:#e0e0e0;padding:1rem;overflow:auto;border-radius:6px;font-size:.8rem}")
          .Append(".banner{padding:.75rem 1rem;border-radius:6px;margin:1rem 0;font-weight:600}")
          .Append(".banner.ok{background:#e6f4ea;color:#1e7e34;border:1px solid #b7dfc1}")
          .Append(".banner.error{background:#fdecea;color:#b71c1c;border:1px solid #f5c6cb}")
          .Append("code{background:#f0f0f0;padding:1px 4px;border-radius:3px}")
          .Append("</style></head><body><h1>").Append(HttpUtility.HtmlEncode(title)).Append("</h1>");
    }

    private static void AppendFooter(StringBuilder sb) => sb.Append("</body></html>");
}
