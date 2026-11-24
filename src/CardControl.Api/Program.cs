using CardControl.Api.Application;
using CardControl.Api.Downstream;
using CardControl.Api.Endpoints;
using CardControl.Api.Errors;
using CardControl.Api.Security;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(kestrel =>
{
    // Don't advertise the server stack.
    kestrel.AddServerHeader = false;
    // A lock/unlock body is well under 2 KB; refuse anything big before reading it.
    kestrel.Limits.MaxRequestBodySize = 16 * 1024;
});

// Malformed JSON / wrong content type: a plain 400 (turned into Problem Details by UseStatusCodePages)
// rather than an exception, in every environment.
builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = false);

// Polly's built-in logging records exception messages (which could carry request data such as a PAN) at
// Information level. It's switched off here, in code so configuration can't re-enable it by accident;
// resilience events are logged by our own callbacks instead (see AddDownstreamClients). Polly metrics are unaffected.
builder.Logging.AddFilter("Polly", LogLevel.None);

builder.Services.AddApiProblemDetails();
builder.Services.AddPartnerAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddPanDecryption(builder.Configuration);
builder.Services.AddDownstreamClients(builder.Configuration);
builder.Services.AddCardControl(builder.Configuration);
builder.Services.AddOpenApi();

var app = builder.Build();

// Load the PAN decryption keys now: a missing or unusable key should stop start-up, not fail a partner call.
_ = app.Services.GetRequiredService<PanKeyRing>();

// Exception handler first, so it wraps everything. It logs exceptions itself without their messages,
// so the middleware's own diagnostics (which log the full exception) are suppressed.
app.UseExceptionHandler(new ExceptionHandlerOptions { SuppressDiagnosticsCallback = _ => true });

// Empty-bodied error responses (404 for unknown routes, 401/403 challenges, 405, 415...) also become Problem Details.
app.UseStatusCodePages();

// TLS: in AKS, TLS is terminated at the ingress (and mTLS inside the mesh), so the app doesn't
// redirect to HTTPS itself. Locally it runs on plain HTTP alongside the starter kit stubs.

app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
}

app.MapGet("/healthz", () => TypedResults.Ok(new { status = "ok" }))
    .AllowAnonymous()
    .WithName("Health")
    .ExcludeFromDescription();

app.MapCardEndpoints();

app.Run();

// Exposed for WebApplicationFactory in the integration tests.
public partial class Program;
