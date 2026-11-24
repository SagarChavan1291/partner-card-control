using System.Net;
using System.Text.Json;
using CardControl.Api.Tests.Infrastructure;

namespace CardControl.Api.Tests;

/// <summary>
/// End to end through the API: real auth, real JWE decryption, real typed clients, against
/// <see cref="FakeBank"/> (the stubs' contracts, in memory). Seed cards:
/// 4111 CORP-1001 Active, 4242 CORP-1001 Locked, 1881 CORP-2002 Active, 5556 CORP-2002 Cancelled,
/// 3155 CORP-3003 Active, 3220 no corporate Active. The partner is enabled for CORP-1001 and CORP-2002.
/// </summary>
public sealed class CardControlFlowTests : IClassFixture<CardControlApiFactory>
{
    private const string Lock = "/v1/cards/lock";
    private const string Unlock = "/v1/cards/unlock";

    private readonly CardControlApiFactory _api;
    private readonly HttpClient _client;

    public CardControlFlowTests(CardControlApiFactory api)
    {
        _api = api;
        _api.Bank.Reset();
        _client = api.CreateClient(api.Tokens.CreateWithWriteRole());
    }

    // ---- Happy paths ----

    [Fact]
    public async Task Locks_an_active_card_and_returns_its_new_state()
    {
        var response = await Send(Lock, "4111111111111111", "CORP-1001");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Json();
        Assert.Equal("Lock", body.GetProperty("action").GetString());
        Assert.Equal("Locked", body.GetProperty("status").GetString());
        Assert.Equal("CORP-1001", body.GetProperty("corporateId").GetString());
        Assert.Equal("411111******1111", body.GetProperty("maskedPan").GetString());
        Assert.True(body.TryGetProperty("updatedAt", out _));
        Assert.Equal("Locked", _api.Bank.StatusOf("ctok_8f3a1c2e9b7d4a01"));
    }

    [Fact]
    public async Task Response_exposes_neither_the_card_token_nor_the_cardholder()
    {
        var text = await (await Send(Lock, "4111111111111111", "CORP-1001")).Content.ReadAsStringAsync();

        Assert.DoesNotContain("ctok_", text);
        Assert.DoesNotContain("Aroha", text);
    }

    [Fact]
    public async Task Unlocks_a_locked_card()
    {
        var response = await Send(Unlock, "4242424242424242", "CORP-1001");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Active", (await response.Json()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Works_for_the_partners_other_corporate()
    {
        var response = await Send(Lock, "4012888888881881", "CORP-2002");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Lock_then_unlock_round_trips()
    {
        Assert.Equal(HttpStatusCode.OK, (await Send(Lock, "4111111111111111", "CORP-1001")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(Unlock, "4111111111111111", "CORP-1001")).StatusCode);
        Assert.Equal("Active", _api.Bank.StatusOf("ctok_8f3a1c2e9b7d4a01"));
    }

    // ---- Audit ----

    [Fact]
    public async Task Passes_the_initiating_user_and_channel_to_the_card_platform()
    {
        await Send(Lock, "4111111111111111", "CORP-1001", initiatingUser: "  jane.smith@acme.example  ");

        var entry = Assert.Single(_api.Bank.Audit);
        Assert.Equal("Lock", entry.Action);
        Assert.Equal("Success", entry.Outcome);
        Assert.Equal("jane.smith@acme.example", entry.InitiatingUser);   // trimmed
        Assert.Equal("partner-app", entry.Channel);                      // the token's azp
    }

    [Fact]
    public async Task Rejected_changes_are_still_audited_against_the_initiating_user()
    {
        await Send(Lock, "4242424242424242", "CORP-1001", initiatingUser: "bob@acme.example");     // already locked
        await Send(Unlock, "4000056655665556", "CORP-2002", initiatingUser: "carol@acme.example"); // cancelled

        Assert.Collection(_api.Bank.Audit,
            e => Assert.Equal(("CardAlreadyLocked", "bob@acme.example"), (e.Outcome, e.InitiatingUser)),
            e => Assert.Equal(("CardCancelled", "carol@acme.example"), (e.Outcome, e.InitiatingUser)));
    }

    // ---- Card platform verdicts ----

    [Fact]
    public async Task Locking_a_locked_card_is_409()
    {
        await AssertProblem(await Send(Lock, "4242424242424242", "CORP-1001"), HttpStatusCode.Conflict, "CardAlreadyLocked");
    }

    [Fact]
    public async Task Unlocking_an_active_card_is_409()
    {
        await AssertProblem(await Send(Unlock, "4111111111111111", "CORP-1001"), HttpStatusCode.Conflict, "CardNotLocked");
    }

    [Theory]
    [InlineData(Lock)]
    [InlineData(Unlock)]
    public async Task Changing_a_cancelled_card_is_422(string route)
    {
        await AssertProblem(await Send(route, "4000056655665556", "CORP-2002"), HttpStatusCode.UnprocessableEntity, "CardCancelled");
    }

    // ---- Entitlement ----

    [Theory]
    [InlineData("CORP-3003")]
    [InlineData("CORP-9999")]
    [InlineData("corp-1001")]   // exact match only
    [InlineData("CORP-1001 ")]
    public async Task Corporate_the_partner_is_not_enabled_for_is_403_before_the_pan_is_even_decrypted(string corporateId)
    {
        var response = await Send(Lock, "4000002500003155", corporateId);

        await AssertProblem(response, HttpStatusCode.Forbidden, "CorporateNotEnabled");
        Assert.Empty(_api.Bank.Requests);
    }

    [Fact]
    public async Task Card_of_a_corporate_the_partner_does_not_manage_is_404_and_never_changed()
    {
        // CORP-3003's card, claimed as CORP-1001's.
        var response = await Send(Lock, "4000002500003155", "CORP-1001");

        await AssertProblem(response, HttpStatusCode.NotFound, "CardNotFound");
        Assert.Empty(_api.Bank.RequestsTo("/cards/ctok_3a7f9c1e5b2d8a05/lock"));
        Assert.Equal("Active", _api.Bank.StatusOf("ctok_3a7f9c1e5b2d8a05"));
    }

    [Fact]
    public async Task Card_of_the_partners_other_corporate_named_with_the_wrong_corporate_is_404()
    {
        // CORP-2002's card, claimed as CORP-1001's: both enabled, but the request must name the right one.
        var response = await Send(Lock, "4012888888881881", "CORP-1001");

        await AssertProblem(response, HttpStatusCode.NotFound, "CardNotFound");
        Assert.Equal("Active", _api.Bank.StatusOf("ctok_5d1c8a3f7e2b9c03"));
    }

    [Fact]
    public async Task Card_with_no_corporate_is_404_and_never_changed()
    {
        var response = await Send(Lock, "4000000000003220", "CORP-1001");

        await AssertProblem(response, HttpStatusCode.NotFound, "CardNotFound");
        Assert.Empty(_api.Bank.RequestsTo("/cards/ctok_7c2e5a9f3d1b6c06/lock"));
    }

    [Fact]
    public async Task Unknown_card_is_404()
    {
        // Valid Luhn, not in the seed data: the tokeniser mints a token the card platform doesn't know.
        await AssertProblem(await Send(Lock, "5555555555554444", "CORP-1001"), HttpStatusCode.NotFound, "CardNotFound");
    }

    [Fact]
    public async Task Not_yours_and_does_not_exist_are_indistinguishable()
    {
        var notYours = await (await Send(Lock, "4000002500003155", "CORP-1001")).Json();
        var unknown = await (await Send(Lock, "5555555555554444", "CORP-1001")).Json();

        Assert.Equal(Strip(unknown), Strip(notYours));

        static string Strip(JsonElement problem) =>
            string.Join("|", problem.EnumerateObject().Where(p => p.Name != "traceId").Select(p => $"{p.Name}={p.Value}"));
    }

    [Fact]
    public async Task Client_without_an_entitlement_entry_is_refused()
    {
        // Has the role, but isn't a client the bank has enabled for any corporate.
        var client = _api.CreateClient(_api.Tokens.Create(roles: [TestTokens.WriteRole], azp: "some-other-partner"));

        var response = await client.SendAsync(ApiRequests.Change(Lock, _api.EncryptPan("4111111111111111"), "CORP-1001"));

        await AssertProblem(response, HttpStatusCode.Forbidden, "CorporateNotEnabled");
    }

    // ---- Encrypted PAN ----

    [Theory]
    [InlineData("not-a-jwe")]
    [InlineData("a.b.c.d.e")]
    [InlineData("4111111111111111")]   // the clear PAN itself is not accepted
    public async Task Garbage_instead_of_a_jwe_is_400(string encryptedPan)
    {
        var response = await _client.SendAsync(ApiRequests.Change(Lock, encryptedPan, "CORP-1001"));

        await AssertProblem(response, HttpStatusCode.BadRequest, "InvalidEncryptedPan");
        Assert.Empty(_api.Bank.Requests);
    }

    [Fact]
    public async Task Jwe_for_someone_elses_key_is_400()
    {
        using var otherKey = System.Security.Cryptography.RSA.Create(3072);
        var jwe = TestJwe.Encrypt("4111111111111111", otherKey);

        var response = await _client.SendAsync(ApiRequests.Change(Lock, jwe, "CORP-1001"));

        await AssertProblem(response, HttpStatusCode.BadRequest, "InvalidEncryptedPan");
    }

    [Fact]
    public async Task Decrypted_value_that_is_not_a_valid_pan_is_400_invalid_pan()
    {
        var response = await Send(Lock, "4111111111111112", "CORP-1001");

        await AssertProblem(response, HttpStatusCode.BadRequest, "InvalidPan");
        Assert.Empty(_api.Bank.Requests);
    }

    // ---- Request validation ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Missing_initiating_user_is_400_and_nothing_is_called(string? user)
    {
        var response = await Send(Lock, "4111111111111111", "CORP-1001", initiatingUser: user);

        var problem = await AssertProblem(response, HttpStatusCode.BadRequest, "InvalidRequest");
        Assert.True(problem.GetProperty("errors").TryGetProperty("X-Initiating-User", out _));
        Assert.Empty(_api.Bank.Requests);
    }

    [Fact]
    public async Task Initiating_user_over_128_characters_is_400()
    {
        var response = await Send(Lock, "4111111111111111", "CORP-1001", initiatingUser: new string('a', 129));

        await AssertProblem(response, HttpStatusCode.BadRequest, "InvalidRequest");
    }

    [Fact]
    public async Task Initiating_user_of_exactly_128_characters_is_accepted()
    {
        var response = await Send(Lock, "4111111111111111", "CORP-1001", initiatingUser: new string('a', 128));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task More_than_one_initiating_user_is_400()
    {
        var request = ApiRequests.Change(Lock, _api.EncryptPan("4111111111111111"), "CORP-1001", initiatingUser: "a@x");
        request.Headers.TryAddWithoutValidation("X-Initiating-User", "b@x");

        await AssertProblem(await _client.SendAsync(request), HttpStatusCode.BadRequest, "InvalidRequest");
    }

    [Theory]
    [InlineData("""{"corporateId":"CORP-1001"}""", "encryptedPan")]
    [InlineData("""{"encryptedPan":"x.y.z.a.b"}""", "corporateId")]
    [InlineData("""{"encryptedPan":"","corporateId":""}""", "encryptedPan")]
    [InlineData("""{}""", "corporateId")]
    public async Task Missing_body_fields_are_400_with_field_errors(string json, string field)
    {
        var response = await _client.SendAsync(ApiRequests.RawJson(Lock, json));

        var problem = await AssertProblem(response, HttpStatusCode.BadRequest, "InvalidRequest");
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _));
    }

    [Fact]
    public async Task Overlong_corporate_id_is_400()
    {
        await AssertProblem(await Send(Lock, "4111111111111111", new string('C', 65)), HttpStatusCode.BadRequest, "InvalidRequest");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"encryptedPan":""")]
    [InlineData("""{"encryptedPan":123,"corporateId":"CORP-1001"}""")]
    public async Task Malformed_json_is_400_problem_details(string json)
    {
        var response = await _client.SendAsync(ApiRequests.RawJson(Lock, json));

        await AssertProblem(response, HttpStatusCode.BadRequest, "InvalidRequest");
    }

    [Fact]
    public async Task Missing_body_is_400()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Lock);
        request.Headers.Add("X-Initiating-User", "jane");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Non_json_content_type_is_415()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Lock) { Content = new StringContent("encryptedPan=x", null, "text/plain") };
        request.Headers.Add("X-Initiating-User", "jane");

        await AssertProblem(await _client.SendAsync(request), HttpStatusCode.UnsupportedMediaType, "UnsupportedMediaType");
    }

    [Fact]
    public async Task Get_is_405()
    {
        await AssertProblem(await _client.GetAsync(Lock), HttpStatusCode.MethodNotAllowed, "MethodNotAllowed");
    }

    // ---- Downstream failures ----

    [Fact]
    public async Task Card_platform_unavailable_is_503()
    {
        _api.Bank.Fault = (req, _) => Task.FromResult(req.RequestUri!.Host == "cards.test"
            ? FakeBank.Problem(HttpStatusCode.ServiceUnavailable, "ServiceUnavailable")
            : null);

        await AssertProblem(await Send(Lock, "4111111111111111", "CORP-1001"), HttpStatusCode.ServiceUnavailable, "ServiceUnavailable");
    }

    [Fact]
    public async Task Tokeniser_unreachable_is_503()
    {
        _api.Bank.Fault = (req, _) => req.RequestUri!.Host == "tokenisation.test"
            ? throw new HttpRequestException("Connection refused")
            : Task.FromResult<HttpResponseMessage?>(null);

        await AssertProblem(await Send(Lock, "4111111111111111", "CORP-1001"), HttpStatusCode.ServiceUnavailable, "ServiceUnavailable");
    }

    [Fact]
    public async Task Off_contract_downstream_answer_is_502()
    {
        _api.Bank.Fault = (req, _) => Task.FromResult(req.RequestUri!.AbsolutePath.EndsWith("/lock")
            ? FakeBank.Problem(HttpStatusCode.BadRequest, "InvalidRequest")
            : null);

        await AssertProblem(await Send(Lock, "4111111111111111", "CORP-1001"), HttpStatusCode.BadGateway, "BadGateway");
    }

    // ---- Helpers ----

    private Task<HttpResponseMessage> Send(string route, string pan, string corporateId, string? initiatingUser = ApiRequests.DefaultUser) =>
        _client.SendAsync(ApiRequests.Change(route, _api.EncryptPan(pan), corporateId, initiatingUser));

    private static async Task<JsonElement> AssertProblem(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Json();
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.Equal((int)status, problem.GetProperty("status").GetInt32());
        Assert.True(problem.TryGetProperty("traceId", out _));
        return problem;
    }
}
