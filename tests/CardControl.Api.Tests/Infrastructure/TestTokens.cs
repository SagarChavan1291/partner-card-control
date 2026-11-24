using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CardControl.Api.Tests.Infrastructure;

/// <summary>
/// Issues access tokens shaped like the mock identity provider's (RS256, kid, typ JWT, Entra-style claims),
/// signed with a key generated per test run. The test host trusts only <see cref="SigningKey"/>.
/// </summary>
public sealed class TestTokens
{
    public const string Issuer = "http://localhost:5090";
    public const string Audience = "api://card-control";
    public const string WriteRole = "CardControl.Write";

    private static readonly JsonWebTokenHandler Handler = new() { SetDefaultTimesOnTokenCreation = false };

    public RsaSecurityKey SigningKey { get; } = NewRsaKey("test-signing-key");

    public static RsaSecurityKey NewRsaKey(string kid) => new(RSA.Create(2048)) { KeyId = kid };

    public string Create(
        string[]? roles = null,
        string issuer = Issuer,
        string audience = Audience,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? expires = null,
        SecurityKey? signWith = null,
        string signingAlgorithm = SecurityAlgorithms.RsaSha256,
        bool includeExpiry = true)
    {
        var now = DateTimeOffset.UtcNow;
        var nbf = notBefore ?? now;
        var claims = new Dictionary<string, object>
        {
            ["sub"] = "3b8f2c6e-9a1d-4f7b-8e2c-5d6a7b8c9d01",
            ["azp"] = "partner-app",
            ["azpacr"] = "1",
            ["tid"] = "7d2e4c1a-5b3f-4e8a-9c6d-1f0a2b3c4d5e",
            ["jti"] = Guid.NewGuid().ToString(),
            ["ver"] = "2.0",
        };
        if (roles is { Length: > 0 })
            claims["roles"] = roles;

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = nbf.UtcDateTime,
            NotBefore = nbf.UtcDateTime,
            Expires = includeExpiry ? (expires ?? now.AddHours(1)).UtcDateTime : null,
            Claims = claims,
            TokenType = "JWT",
            SigningCredentials = new SigningCredentials(signWith ?? SigningKey, signingAlgorithm),
        };
        return Handler.CreateToken(descriptor);
    }

    public string CreateWithWriteRole() => Create(roles: [WriteRole]);

    /// <summary>An unsigned token ("alg":"none"), the classic signature-bypass attempt.</summary>
    public static string CreateUnsigned(string[] roles)
    {
        var header = Base64UrlEncoder.Encode("""{"alg":"none","typ":"JWT"}""");
        var exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var nbf = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var payload = Base64UrlEncoder.Encode(
            $$"""{"iss":"{{Issuer}}","aud":"{{Audience}}","roles":["{{string.Join("\",\"", roles)}}"],"nbf":{{nbf}},"exp":{{exp}}}""");
        return $"{header}.{payload}.";
    }

    /// <summary>Re-encodes a signed token's payload (keeping the original signature), as an attacker would.</summary>
    public static string TamperPayload(string token, Func<string, string> edit)
    {
        var parts = token.Split('.');
        var json = Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(parts[1]));
        parts[1] = Base64UrlEncoder.Encode(edit(json));
        return string.Join('.', parts);
    }
}
