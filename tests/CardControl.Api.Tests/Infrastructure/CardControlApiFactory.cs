using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace CardControl.Api.Tests.Infrastructure;

/// <summary>
/// Hosts the API in-process with all log output captured. The real JWT validation rules are kept;
/// only the source of signing keys is swapped: instead of fetching the IdP's JWKS, the API trusts
/// <see cref="TestTokens.SigningKey"/>, so tests don't need the mock identity provider running.
/// </summary>
public class CardControlApiFactory : WebApplicationFactory<Program>
{
    public CapturingLoggerProvider Logs { get; } = new();

    public TestTokens Tokens { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureLogging(logging =>
        {
            logging.AddProvider(Logs);
            logging.SetMinimumLevel(LogLevel.Trace);
        });

        builder.ConfigureServices(services =>
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                var configuration = new OpenIdConnectConfiguration { Issuer = TestTokens.Issuer };
                configuration.SigningKeys.Add(Tokens.SigningKey);
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
            }));
    }

    public HttpClient CreateClient(string? bearerToken)
    {
        var client = CreateClient();
        if (bearerToken is not null)
            client.DefaultRequestHeaders.Authorization = new("Bearer", bearerToken);
        return client;
    }
}
