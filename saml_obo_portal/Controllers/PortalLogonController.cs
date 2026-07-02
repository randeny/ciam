using System.Text;
using Microsoft.AspNetCore.Mvc;
using OpenIdServer.Configuration;

namespace OpenIdServer.Controllers;

/// <summary>
/// Backend for the native-auth SPA hosted at <c>/portallogon</c>.
///
/// Mirrors the standalone Express backend that previously served the SPA:
///   * <c>POST /portallogon/native/{**path}</c> proxies the MSAL custom-auth
///     (native authentication) calls to the CIAM <c>ciamlogin.com</c> host
///     (the browser cannot call those endpoints directly — no CORS headers).
///   * <c>POST /portallogon/sso</c> takes the user's access token, performs the
///     On-Behalf-Of exchange for a SAML2 assertion, wraps it in a
///     <c>&lt;samlp:Response&gt;</c> and returns an auto-submitting form that
///     POSTs the SAMLResponse to the SAML SP Assertion Consumer Service.
/// </summary>
[ApiController]
[Route("portallogon")]
public class PortalLogonController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PortalLogonController> _logger;
    private readonly PortalLogonOptions _options;

    public PortalLogonController(
        IHttpClientFactory httpClientFactory,
        ILogger<PortalLogonController> logger,
        PortalLogonOptions options)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _options = options;
    }

    /// <summary>
    /// Native-auth CORS proxy. The SPA calls
    /// <c>/portallogon/native/oauth2/v2.0/&lt;op&gt;</c>; this forwards to
    /// <c>https://{NativeAuthHost}/{tenantId}/oauth2/v2.0/&lt;op&gt;</c>.
    /// </summary>
    [HttpPost("native/{**path}")]
    public async Task<IActionResult> NativeProxy(string path)
    {
        var client = _httpClientFactory.CreateClient("NativeAuthProxy");
        var upstreamUrl =
            $"https://{_options.NativeAuthHost}/{_options.TenantId}/{path}";

        _logger.LogInformation("PortalLogon native proxy: POST {Upstream}", upstreamUrl);

        Request.EnableBuffering();
        Request.Body.Position = 0;
        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms);
        var bodyBytes = ms.ToArray();

        using var upstream = new HttpRequestMessage(HttpMethod.Post, upstreamUrl)
        {
            Content = new ByteArrayContent(bodyBytes),
        };
        upstream.Content.Headers.ContentType =
            System.Net.Http.Headers.MediaTypeHeaderValue.Parse(
                string.IsNullOrEmpty(Request.ContentType)
                    ? "application/x-www-form-urlencoded"
                    : Request.ContentType);

        var response = await client.SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead);
        var text = await response.Content.ReadAsStringAsync();
        var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("PortalLogon native proxy: upstream {Status}. Body: {Body}",
                (int)response.StatusCode, text);
        }

        return new ContentResult
        {
            StatusCode = (int)response.StatusCode,
            ContentType = contentType,
            Content = text,
        };
    }

    /// <summary>
    /// Server-side SSO: OBO -> SAML2 assertion -> auto-POST to the SP ACS.
    /// The SPA POSTs the user's access token here (form field "accessToken").
    /// </summary>
    [HttpPost("sso")]
    [Consumes("application/x-www-form-urlencoded")]
    public async Task<IActionResult> Sso([FromForm] string? accessToken)
    {
        var token = accessToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            var auth = Request.Headers.Authorization.ToString();
            if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                token = auth["Bearer ".Length..].Trim();
            }
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return Html(400, RenderError("Missing access token.", "No \"accessToken\" was provided to /portallogon/sso."));
        }

        if (string.IsNullOrWhiteSpace(_options.OboClientSecret))
        {
            _logger.LogError("PortalLogon SSO: OboClientSecret is not configured.");
            return Html(500, RenderError("Server not configured", "The OBO client secret is not configured (set App Service setting PortalLogon__OboClientSecret)."));
        }

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
            ["client_id"] = _options.OboClientId,
            ["client_secret"] = _options.OboClientSecret,
            ["assertion"] = token,
            ["scope"] = _options.OboScope,
            ["requested_token_use"] = "on_behalf_of",
            ["requested_token_type"] = "urn:ietf:params:oauth:token-type:saml2",
        };

        var client = _httpClientFactory.CreateClient("BffObo");
        string raw;
        HttpResponseMessage oboRes;
        try
        {
            // Mark this as server-to-server so an upstream proxy/Front Door rule can
            // strip any Origin header (AADSTS9002326 is raised when the confidential
            // OBO token request carries an Origin header). Ensure no Origin is sent.
            using var oboReq = new HttpRequestMessage(HttpMethod.Post, _options.OboTokenUrl)
            {
                Content = new FormUrlEncodedContent(form),
            };
            oboReq.Headers.Remove("Origin");
            oboReq.Headers.TryAddWithoutValidation("X-S2S-OBO", "1");

            oboRes = await client.SendAsync(oboReq);
            raw = await oboRes.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PortalLogon SSO: OBO request failed.");
            return Html(500, RenderError("SSO error", ex.Message));
        }

        if (!oboRes.IsSuccessStatusCode)
        {
            _logger.LogWarning("PortalLogon SSO: OBO exchange failed {Status}: {Body}", (int)oboRes.StatusCode, raw);
            return Html(400, RenderError("OBO exchange failed", $"{(int)oboRes.StatusCode}: {raw}"));
        }

        string? issuedToken = null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("access_token", out var at))
            {
                issuedToken = at.GetString();
            }
        }
        catch
        {
            // not JSON
        }

        if (string.IsNullOrWhiteSpace(issuedToken))
        {
            return Html(400, RenderError("No token returned", "The OBO response did not contain an access_token."));
        }

        var samlResponseB64 = BuildSamlResponse(issuedToken);
        if (samlResponseB64 is null)
        {
            return Html(400, RenderError(
                "Unexpected token format",
                "The OBO endpoint did not return a SAML 2.0 assertion. Confirm the confidential client is configured to issue SAML2 tokens (requested_token_type=urn:ietf:params:oauth:token-type:saml2)."));
        }

        return Html(200, AutoPostForm(_options.AcsUrl, samlResponseB64));
    }

    // -----------------------------------------------------------------------
    // Helpers (ported from the Express backend)
    // -----------------------------------------------------------------------

    private string? BuildSamlResponse(string issuedToken)
    {
        // JWT (three dot-separated parts) means SAML was NOT issued.
        if (System.Text.RegularExpressions.Regex.IsMatch(issuedToken, @"^[\w-]+\.[\w-]+\.[\w-]+$"))
        {
            return null;
        }

        string? xml = null;
        try
        {
            var decoded = Encoding.UTF8.GetString(Base64UrlToBytes(issuedToken));
            if (decoded.Contains("Assertion") || decoded.Contains("Response"))
            {
                xml = decoded;
            }
        }
        catch
        {
            // not base64
        }

        if (xml is null && (issuedToken.Contains("Assertion") || issuedToken.Contains("Response")))
        {
            xml = issuedToken; // already raw XML
        }

        if (xml is null)
        {
            return null;
        }

        string responseXml;
        if (System.Text.RegularExpressions.Regex.IsMatch(xml, @"<(\w+:)?Response[\s>]"))
        {
            responseXml = xml;
        }
        else
        {
            var id = "_" + Guid.NewGuid();
            var issueInstant = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            responseXml =
                $"<samlp:Response xmlns:samlp=\"urn:oasis:names:tc:SAML:2.0:protocol\" " +
                $"ID=\"{id}\" Version=\"2.0\" IssueInstant=\"{issueInstant}\" Destination=\"{_options.AcsUrl}\">" +
                $"<samlp:Status><samlp:StatusCode Value=\"urn:oasis:names:tc:SAML:2.0:status:Success\"/></samlp:Status>" +
                $"{xml}</samlp:Response>";
        }

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(responseXml));
    }

    private static byte[] Base64UrlToBytes(string s)
    {
        var b64 = s.Replace('-', '+').Replace('_', '/');
        switch (b64.Length % 4)
        {
            case 2: b64 += "=="; break;
            case 3: b64 += "="; break;
        }
        return Convert.FromBase64String(b64);
    }

    private static string EscapeHtml(string s) => s
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;")
        .Replace("'", "&#39;");

    private static string AutoPostForm(string acsUrl, string samlResponseB64) =>
        "<!doctype html>\n" +
        "<html><head><meta charset=\"utf-8\"><title>Signing in&hellip;</title></head>\n" +
        "<body onload=\"document.forms[0].submit()\">\n" +
        "  <noscript><p>JavaScript is disabled. Click the button to continue.</p></noscript>\n" +
        "  <p style=\"font-family:sans-serif\">Signing you in to the SAML application&hellip;</p>\n" +
        $"  <form method=\"POST\" action=\"{EscapeHtml(acsUrl)}\">\n" +
        $"    <input type=\"hidden\" name=\"SAMLResponse\" value=\"{EscapeHtml(samlResponseB64)}\" />\n" +
        "    <noscript><button type=\"submit\">Continue</button></noscript>\n" +
        "  </form>\n" +
        "</body></html>";

    private static string RenderError(string title, string detail) =>
        "<!doctype html>\n" +
        $"<html><head><meta charset=\"utf-8\"><title>{EscapeHtml(title)}</title></head>\n" +
        "<body style=\"font-family:sans-serif;max-width:640px;margin:48px auto\">\n" +
        $"  <h2 style=\"color:#b00020\">{EscapeHtml(title)}</h2>\n" +
        $"  <pre style=\"white-space:pre-wrap;background:#f5f5f5;padding:16px;border-radius:8px\">{EscapeHtml(detail)}</pre>\n" +
        "  <p><a href=\"/portallogon/\">&larr; Back to sign-in</a></p>\n" +
        "</body></html>";

    private ContentResult Html(int status, string html) => new()
    {
        StatusCode = status,
        ContentType = "text/html; charset=utf-8",
        Content = html,
    };
}
