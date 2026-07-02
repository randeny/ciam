using Microsoft.AspNetCore.HttpOverrides;
using OpenIdServer.Configuration;
using OpenIdServer.Saml;

var builder = WebApplication.CreateBuilder(args);

// ── Strongly-typed configuration ──────────────────────────────────────────
var corsOptions = BindRequiredSection<CorsOptions>(builder.Configuration, CorsOptions.SectionName);
var samlOptions = BindRequiredSection<SamlOptions>(builder.Configuration, SamlOptions.SectionName);
var portalLogonOptions = BindRequiredSection<PortalLogonOptions>(builder.Configuration, PortalLogonOptions.SectionName);

builder.Services.AddSingleton(corsOptions);
builder.Services.AddSingleton(samlOptions);
builder.Services.AddSingleton(portalLogonOptions);

// ── SAML support services ─────────────────────────────────────────────────
builder.Services.AddSingleton<IReplayCache, InMemoryReplayCache>();
builder.Services.AddSingleton<ISamlResultStore, InMemorySamlResultStore>();

// ── HTTP clients used by the OBO / native-auth backend ────────────────────
builder.Services.AddHttpClient("BffObo");
builder.Services.AddHttpClient("NativeAuthProxy")
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AllowAutoRedirect = false,
    });

builder.Services.AddControllers();

builder.Services.AddCors(options =>
{
    options.AddPolicy("SpaClient", policy =>
    {
        policy.WithOrigins(corsOptions.AllowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

var app = builder.Build();

// Respect Front Door / App Service forwarded scheme + host so generated URLs are https.
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
});

// Allow controllers to re-read the request body if model binding consumed it.
app.Use(async (context, next) =>
{
    context.Request.EnableBuffering();
    await next();
});

app.UseDefaultFiles(new DefaultFilesOptions
{
    DefaultFileNames = new List<string> { "index.html" },
});
app.UseStaticFiles();
app.UseRouting();
app.UseCors("SpaClient");
app.MapControllers();

app.Run();

// ── Helpers ───────────────────────────────────────────────────────────────
static T BindRequiredSection<T>(IConfiguration configuration, string sectionName) where T : new()
{
    var section = configuration.GetSection(sectionName);
    if (!section.Exists())
    {
        throw new InvalidOperationException($"Required configuration section '{sectionName}' is missing.");
    }

    var options = new T();
    section.Bind(options);
    return options;
}
