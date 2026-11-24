using System.Net;
using System.Text.Json;
using CardControl.Api.Tests.Infrastructure;
using Microsoft.IdentityModel.Tokens;

namespace CardControl.Api.Tests;

/// <summary>
/// The lock/unlock endpoints only accept tokens from the bank's IdP, for this API, still in date,
/// carrying the CardControl.Write role. Everything else is 401 (who are you?) or 403 (not allowed).
/// </summary>
public class AuthenticationTests(CardControlApiFactory factory) : IClassFixture<CardControlApiFactory>
{
    public static TheoryData<string> ProtectedRoutes => ["/v1/cards/lock", "/v1/cards/unlock"];

    [Theory]
    [MemberData(nameof(ProtectedRoutes))]
    public async Task Valid_token_with_write_role_is_let_through(string route)
    {
        var response = await Post(route, factory.Tokens.CreateWithWriteRole());

        AssertLetThrough(response);
    }

    [Theory]
    [MemberData(nameof(ProtectedRoutes))]
    public async Task Missing_token_is_401_with_bearer_challenge(string route)
    {
        var response = await Post(route, token: null);

        await AssertProblem(response, HttpStatusCode.Unauthorized, "Unauthorized");
        Assert.Contains(response.Headers.WwwAuthenticate, h => h.Scheme == "Bearer");
    }

    [Fact]
    public async Task Token_without_any_role_is_403()
    {
        // What the partner-readonly client gets: a perfectly valid token, but no roles claim.
        var response = await Post("/v1/cards/lock", factory.Tokens.Create(roles: null));

        await AssertProblem(response, HttpStatusCode.Forbidden, "Forbidden");
    }

    [Fact]
    public async Task Token_with_a_different_role_is_403()
    {
        var response = await Post("/v1/cards/lock", factory.Tokens.Create(roles: ["CardControl.Read"]));

        await AssertProblem(response, HttpStatusCode.Forbidden, "Forbidden");
    }

    [Fact]
    public async Task Role_check_is_case_sensitive()
    {
        var response = await Post("/v1/cards/lock", factory.Tokens.Create(roles: ["cardcontrol.write"]));

        await AssertProblem(response, HttpStatusCode.Forbidden, "Forbidden");
    }

    [Fact]
    public async Task Expired_token_is_401()
    {
        var token = factory.Tokens.Create(
            roles: [TestTokens.WriteRole],
            notBefore: DateTimeOffset.UtcNow.AddHours(-2),
            expires: DateTimeOffset.UtcNow.AddMinutes(-5));

        await AssertProblem(await Post("/v1/cards/lock", token), HttpStatusCode.Unauthorized, "Unauthorized");
    }

    [Fact]
    public async Task Token_expired_within_clock_skew_is_still_accepted()
    {
        var token = factory.Tokens.Create(
            roles: [TestTokens.WriteRole],
            notBefore: DateTimeOffset.UtcNow.AddHours(-1),
            expires: DateTimeOffset.UtcNow.AddSeconds(-10));

        AssertLetThrough(await Post("/v1/cards/lock", token));
    }

    [Fact]
    public async Task Token_not_yet_valid_is_401()
    {
        var token = factory.Tokens.Create(
            roles: [TestTokens.WriteRole],
            notBefore: DateTimeOffset.UtcNow.AddMinutes(10),
            expires: DateTimeOffset.UtcNow.AddHours(1));

        await AssertProblem(await Post("/v1/cards/lock", token), HttpStatusCode.Unauthorized, "Unauthorized");
    }

    [Fact]
    public async Task Token_without_expiry_is_401()
    {
        var token = factory.Tokens.Create(roles: [TestTokens.WriteRole], includeExpiry: false);

        await AssertProblem(await Post("/v1/cards/lock", token), HttpStatusCode.Unauthorized, "Unauthorized");
    }

    [Fact]
    public async Task Token_for_another_audience_is_401()
    {
        // What the IdP issues for scope=api://other-api/.default.
        var token = factory.Tokens.Create(roles: [TestTokens.WriteRole], audience: "api://other-api");

        await AssertProblem(await Post("/v1/cards/lock", token), HttpStatusCode.Unauthorized, "Unauthorized");
    }

    [Fact]
    public async Task Token_from_another_issuer_is_401()
    {
        var token = factory.Tokens.Create(roles: [TestTokens.WriteRole], issuer: "http://evil.example");

        await AssertProblem(await Post("/v1/cards/lock", token), HttpStatusCode.Unauthorized, "Unauthorized");
    }

    [Fact]
    public async Task Token_signed_by_an_untrusted_key_is_401_even_with_a_matching_kid()
    {
        var attackerKey = TestTokens.NewRsaKey(factory.Tokens.SigningKey.KeyId);
        var token = factory.Tokens.Create(roles: [TestTokens.WriteRole], signWith: attackerKey);

        await AssertProblem(await Post("/v1/cards/lock", token), HttpStatusCode.Unauthorized, "Unauthorized");
    }

    [Fact]
    public async Task Unsigned_alg_none_token_is_401()
    {
        var token = TestTokens.CreateUnsigned([TestTokens.WriteRole]);

        await AssertProblem(await Post("/v1/cards/lock", token), HttpStatusCode.Unauthorized, "Unauthorized");
    }

    [Fact]
    public async Task Hmac_signed_token_is_401()
    {
        // Algorithm confusion: only RS256 is accepted, whatever key the attacker uses.
        var hmacKey = new SymmetricSecurityKey(new byte[32]) { KeyId = factory.Tokens.SigningKey.KeyId };
        var token = factory.Tokens.Create(roles: [TestTokens.WriteRole], signWith: hmacKey,
            signingAlgorithm: SecurityAlgorithms.HmacSha256);

        await AssertProblem(await Post("/v1/cards/lock", token), HttpStatusCode.Unauthorized, "Unauthorized");
    }

    [Fact]
    public async Task Token_with_role_added_after_signing_is_401()
    {
        var token = TestTokens.TamperPayload(factory.Tokens.Create(roles: null),
            json => json.TrimEnd('}') + $",\"roles\":[\"{TestTokens.WriteRole}\"]}}");

        await AssertProblem(await Post("/v1/cards/lock", token), HttpStatusCode.Unauthorized, "Unauthorized");
    }

    [Theory]
    [InlineData("not-a-jwt")]
    [InlineData("a.b.c")]
    public async Task Malformed_token_is_401(string token)
    {
        await AssertProblem(await Post("/v1/cards/lock", token), HttpStatusCode.Unauthorized, "Unauthorized");
    }

    [Fact]
    public async Task Health_endpoint_needs_no_token()
    {
        var response = await factory.CreateClient(bearerToken: null).GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Rejected_callers_never_reach_the_downstream_services()
    {
        factory.Bank.Reset();

        await Post("/v1/cards/lock", token: null);
        await Post("/v1/cards/lock", factory.Tokens.Create(roles: null));
        await Post("/v1/cards/lock", factory.Tokens.Create(roles: [TestTokens.WriteRole], audience: "api://other-api"));

        Assert.Empty(factory.Bank.Requests);
    }

    /// <summary>A complete, valid lock/unlock request: only the token decides whether it gets through.</summary>
    private Task<HttpResponseMessage> Post(string route, string? token) =>
        factory.CreateClient(token).SendAsync(
            ApiRequests.Change(route, factory.EncryptPan("4111111111111111"), "CORP-1001"));

    // Got past authentication and authorisation; the card's state decides between 200 and 409.
    private static void AssertLetThrough(HttpResponseMessage response) =>
        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict });

    private static async Task AssertProblem(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(code, problem.GetProperty("code").GetString());
    }
}
