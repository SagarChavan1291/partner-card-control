using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;

namespace CardControl.Api.Security;

/// <summary>Settings for validating partner access tokens issued by the bank's OIDC authorisation server.</summary>
public sealed class PartnerAuthOptions
{
    public const string SectionName = "PartnerAuth";

    /// <summary>OIDC authority. Signing keys come from its discovery document / JWKS.</summary>
    [Required, Url]
    public string Authority { get; set; } = "";

    /// <summary>Expected <c>iss</c>. Defaults to the authority without a trailing slash.</summary>
    public string? Issuer { get; set; }

    /// <summary>Expected <c>aud</c>: this API's application ID URI.</summary>
    [Required]
    public string Audience { get; set; } = "";

    /// <summary>Only ever false in local development, where the mock identity provider is plain HTTP.</summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    [Range(0, 300)]
    public int ClockSkewSeconds { get; set; } = 30;

    public string EffectiveIssuer => (Issuer ?? Authority).TrimEnd('/');
}

public static class Policies
{
    /// <summary>Caller's token must carry the <see cref="AppRoles.CardControlWrite"/> app role.</summary>
    public const string CardControlWrite = nameof(CardControlWrite);
}

public static class AppRoles
{
    public const string CardControlWrite = "CardControl.Write";
}

public static class PartnerAuthenticationExtensions
{
    public static IServiceCollection AddPartnerAuthentication(this IServiceCollection services,
        IConfiguration configuration, IHostEnvironment environment)
    {
        var section = configuration.GetSection(PartnerAuthOptions.SectionName);
        services.AddOptions<PartnerAuthOptions>().Bind(section).ValidateDataAnnotations().ValidateOnStart();

        var auth = section.Get<PartnerAuthOptions>() ?? new PartnerAuthOptions();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = auth.Authority;
                options.Audience = auth.Audience;
                // Fails start-up if the authority isn't HTTPS, unless explicitly relaxed (dev only).
                options.RequireHttpsMetadata = auth.RequireHttpsMetadata;
                // Keep the token's claim names (roles, azp, tid...) rather than legacy SOAP URIs.
                options.MapInboundClaims = false;
                // error_description in WWW-Authenticate helps when developing; don't hand it to callers in prod.
                options.IncludeErrorDetails = environment.IsDevelopment();

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = auth.EffectiveIssuer,
                    ValidateAudience = true,
                    ValidAudience = auth.Audience,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    ClockSkew = TimeSpan.FromSeconds(auth.ClockSkewSeconds),
                    ValidateIssuerSigningKey = true,
                    RequireSignedTokens = true,
                    // Pin the algorithm: blocks "alg":"none" and RS/HS confusion.
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                    ValidTypes = ["JWT"],
                    RoleClaimType = "roles",
                    NameClaimType = "azp",
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.CardControlWrite, policy => policy
                .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()
                .RequireRole(AppRoles.CardControlWrite))
            // Secure by default: any endpoint without an explicit policy still needs a valid token.
            .SetFallbackPolicy(new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()
                .Build());

        return services;
    }
}
