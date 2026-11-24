using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using CardControl.Api.Downstream;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace CardControl.Api.Tests.Infrastructure;

/// <summary>
/// Hosts the API in-process with all log output captured. The real JWT validation rules are kept;
/// only the source of signing keys is swapped: instead of fetching the IdP's JWKS, the API trusts
/// <see cref="TestTokens.SigningKey"/>, so tests don't need the mock identity provider running.
/// Likewise the PAN decryption key is generated per run (<see cref="PanKey"/>) instead of read from .dev-keys/,
/// and both downstream services are <see cref="Bank"/>, an in-memory fake behind the real typed HTTP clients.
/// </summary>
public class CardControlApiFactory : WebApplicationFactory<Program>
{
    public CapturingLoggerProvider Logs { get; } = new();

    public TestTokens Tokens { get; } = new();

    /// <summary>The bank's PAN key pair as the API under test holds it. Encrypt with <see cref="EncryptPan"/>.</summary>
    public RSA PanKey { get; } = RSA.Create(3072);

    public string EncryptPan(string pan) => TestJwe.Encrypt(pan, PanKey);

    public FakeBank Bank { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PanEncryption:Keys:0:Kid"] = TestJwe.DefaultKid,
            ["PanEncryption:Keys:0:PrivateKeyPath"] = null,
            ["PanEncryption:Keys:0:PrivateKeyPem"] = PanKey.ExportPkcs8PrivateKeyPem(),
            ["Downstream:Tokenisation:BaseUrl"] = FakeBank.TokenisationBaseUrl,
            ["Downstream:CardPlatform:BaseUrl"] = FakeBank.CardPlatformBaseUrl,
            // Everything, from every category, so leak tests see what the most verbose production setting would log.
            // (appsettings' per-category levels would otherwise override SetMinimumLevel below.)
            ["Logging:LogLevel:Default"] = "Trace",
            ["Logging:LogLevel:Microsoft.AspNetCore"] = "Trace",
            ["Logging:LogLevel:Microsoft.AspNetCore.Authentication"] = "Trace",
        }));
        builder.ConfigureLogging(logging =>
        {
            logging.AddProvider(Logs);
            logging.SetMinimumLevel(LogLevel.Trace);
        });

        builder.ConfigureServices(services =>
        {
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                var configuration = new OpenIdConnectConfiguration { Issuer = TestTokens.Issuer };
                configuration.SigningKeys.Add(Tokens.SigningKey);
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
            });

            // Same typed clients as production; only the bottom of the handler pipeline is swapped.
            services.AddHttpClient<ITokenisationClient, TokenisationClient>()
                .ConfigurePrimaryHttpMessageHandler(() => new NonDisposingHandler(Bank));
            services.AddHttpClient<ICardPlatformClient, CardPlatformClient>()
                .ConfigurePrimaryHttpMessageHandler(() => new NonDisposingHandler(Bank));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            PanKey.Dispose();
    }

    /// <summary>Lets the factory recycle handler pipelines without disposing the shared fake.</summary>
    private sealed class NonDisposingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override void Dispose(bool disposing) { }
    }

    public HttpClient CreateClient(string? bearerToken)
    {
        var client = CreateClient();
        if (bearerToken is not null)
            client.DefaultRequestHeaders.Authorization = new("Bearer", bearerToken);
        return client;
    }
}
