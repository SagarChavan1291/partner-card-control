using CardControl.Api.Errors;

var builder = WebApplication.CreateBuilder(args);

// Don't advertise the server stack.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.AddServerHeader = false);

builder.Services.AddApiProblemDetails();
builder.Services.AddOpenApi();

var app = builder.Build();

// Exception handler first, so it wraps everything. It logs exceptions itself without their messages,
// so the middleware's own diagnostics (which log the full exception) are suppressed.
app.UseExceptionHandler(new ExceptionHandlerOptions { SuppressDiagnosticsCallback = _ => true });

// Empty-bodied error responses (404 for unknown routes, 405, 415...) also become Problem Details.
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// TLS: in AKS, TLS is terminated at the ingress (and mTLS inside the mesh), so the app doesn't
// redirect to HTTPS itself. Locally it runs on plain HTTP alongside the starter kit stubs.

app.MapGet("/healthz", () => TypedResults.Ok(new { status = "ok" }))
    .WithName("Health")
    .ExcludeFromDescription();

app.Run();

// Exposed for WebApplicationFactory in the integration tests.
public partial class Program;
