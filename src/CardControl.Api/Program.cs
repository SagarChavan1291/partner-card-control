using CardControl.Api.Endpoints;
using CardControl.Api.Errors;
using CardControl.Api.Security;

var builder = WebApplication.CreateBuilder(args);

// Don't advertise the server stack.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.AddServerHeader = false);

builder.Services.AddApiProblemDetails();
builder.Services.AddPartnerAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddPanDecryption(builder.Configuration);
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
