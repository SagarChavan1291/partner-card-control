using System.Net;
using System.Text.Json;
using CardControl.Api.Downstream;
using CardControl.Api.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace CardControl.Api.Tests.Downstream;

public class CardPlatformClientTests
{
    private const string Token = "ctok_8f3a1c2e9b7d4a01";

    private const string CardJson = """
        {
          "cardToken": "ctok_8f3a1c2e9b7d4a01",
          "corporateId": "CORP-1001",
          "cardholderName": "Aroha Ngata",
          "maskedPan": "411111******1111",
          "status": "Active",
          "updatedAt": "2026-01-01T00:00:00+00:00"
        }
        """;

    // ---- GET /cards/{token} ----

    [Fact]
    public async Task Gets_a_card_by_token()
    {
        var stub = new StubHttpHandler(_ => StubHttpHandler.Json(HttpStatusCode.OK, CardJson));

        var card = await Client(stub).GetCardAsync(Token, CancellationToken.None);

        Assert.NotNull(card);
        Assert.Equal("CORP-1001", card.CorporateId);
        Assert.Equal(CardStatus.Active, card.Status);
        Assert.Equal("411111******1111", card.MaskedPan);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), card.UpdatedAt);
        var sent = Assert.Single(stub.Requests);
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Equal($"http://cards.test/cards/{Token}", sent.Uri.ToString());
    }

    [Fact]
    public async Task A_card_without_a_corporate_comes_back_with_null_corporate()
    {
        var stub = new StubHttpHandler(_ => StubHttpHandler.Json(HttpStatusCode.OK,
            CardJson.Replace("\"CORP-1001\"", "null")));

        var card = await Client(stub).GetCardAsync(Token, CancellationToken.None);

        Assert.Null(card!.CorporateId);
    }

    [Fact]
    public async Task Unknown_card_is_null()
    {
        var stub = new StubHttpHandler(_ => StubHttpHandler.Problem(HttpStatusCode.NotFound, "CardNotFound"));

        Assert.Null(await Client(stub).GetCardAsync(Token, CancellationToken.None));
    }

    [Theory]
    [InlineData("\"status\": \"Active\"", "\"status\": \"Frozen\"")]           // status we don't know
    [InlineData("\"status\": \"Active\"", "\"status\": 1")]                    // numeric enum
    [InlineData("\"maskedPan\": \"411111******1111\",", "")]                   // required member missing
    [InlineData("\"cardholderName\": \"Aroha Ngata\"", "\"cardholderName\": null")]
    [InlineData("\"updatedAt\": \"2026-01-01T00:00:00+00:00\"", "\"updatedAt\": \"yesterday\"")]
    public async Task Off_contract_cards_are_bad_responses(string from, string to)
    {
        var stub = new StubHttpHandler(_ => StubHttpHandler.Json(HttpStatusCode.OK, CardJson.Replace(from, to)));

        var ex = await Assert.ThrowsAsync<DownstreamException>(() => Client(stub).GetCardAsync(Token, CancellationToken.None));

        Assert.Equal(DownstreamFailure.BadResponse, ex.Failure);
    }

    [Theory]
    [InlineData("../admin/reset")]
    [InlineData("ctok_1/lock")]
    [InlineData("")]
    public async Task Refuses_to_put_a_malformed_token_in_a_url(string token)
    {
        var stub = new StubHttpHandler(_ => StubHttpHandler.Json(HttpStatusCode.OK, CardJson));

        await Assert.ThrowsAsync<ArgumentException>(() => Client(stub).GetCardAsync(token, CancellationToken.None));
        Assert.Empty(stub.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "ServiceUnavailable", DownstreamFailure.Unavailable)]
    [InlineData(HttpStatusCode.BadGateway, null, DownstreamFailure.Unavailable)]
    [InlineData(HttpStatusCode.NotFound, "SomethingElse", DownstreamFailure.BadResponse)]
    [InlineData(HttpStatusCode.BadRequest, "InvalidRequest", DownstreamFailure.BadResponse)]
    public async Task Get_errors_other_than_not_found_are_downstream_failures(HttpStatusCode status, string? code, DownstreamFailure expected)
    {
        var stub = new StubHttpHandler(_ => code is null
            ? new HttpResponseMessage(status) { Content = new StringContent("<html>Bad gateway</html>") }
            : StubHttpHandler.Problem(status, code));

        var ex = await Assert.ThrowsAsync<DownstreamException>(() => Client(stub).GetCardAsync(Token, CancellationToken.None));

        Assert.Equal(expected, ex.Failure);
        Assert.Equal(code, ex.ErrorCode);
    }

    // ---- POST /cards/{token}/lock|unlock ----

    [Theory]
    [InlineData(CardAction.Lock, "lock")]
    [InlineData(CardAction.Unlock, "unlock")]
    public async Task Change_posts_the_initiating_user_and_channel_and_returns_the_updated_card(CardAction action, string segment)
    {
        var stub = new StubHttpHandler(_ => StubHttpHandler.Json(HttpStatusCode.OK, CardJson.Replace("Active", "Locked")));

        var result = await Client(stub).ChangeAsync(Token, action, "jane.smith@acme.example", "partner-app", CancellationToken.None);

        Assert.Equal(CardChangeOutcome.Changed, result.Outcome);
        Assert.Equal(CardStatus.Locked, result.Card!.Status);

        var sent = Assert.Single(stub.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal($"http://cards.test/cards/{Token}/{segment}", sent.Uri.ToString());
        var body = JsonDocument.Parse(sent.Body!).RootElement;
        Assert.Equal("jane.smith@acme.example", body.GetProperty("initiatingUser").GetString());
        Assert.Equal("partner-app", body.GetProperty("channel").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "CardNotFound", CardChangeOutcome.CardNotFound)]
    [InlineData(HttpStatusCode.Conflict, "CardAlreadyLocked", CardChangeOutcome.AlreadyLocked)]
    [InlineData(HttpStatusCode.Conflict, "CardNotLocked", CardChangeOutcome.NotLocked)]
    [InlineData(HttpStatusCode.UnprocessableEntity, "CardCancelled", CardChangeOutcome.Cancelled)]
    public async Task Business_rejections_are_results(HttpStatusCode status, string code, CardChangeOutcome expected)
    {
        var stub = new StubHttpHandler(_ => StubHttpHandler.Problem(status, code));

        var result = await Client(stub).ChangeAsync(Token, CardAction.Lock, "jane", null, CancellationToken.None);

        Assert.Equal(expected, result.Outcome);
        Assert.Null(result.Card);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "ServiceUnavailable", DownstreamFailure.Unavailable)]
    [InlineData(HttpStatusCode.BadRequest, "InvalidRequest", DownstreamFailure.BadResponse)]
    [InlineData(HttpStatusCode.Conflict, "SomethingNew", DownstreamFailure.BadResponse)]
    [InlineData(HttpStatusCode.UnprocessableEntity, "CardAlreadyLocked", DownstreamFailure.BadResponse)]  // known code, wrong status
    public async Task Change_errors_that_are_not_business_outcomes_are_downstream_failures(
        HttpStatusCode status, string code, DownstreamFailure expected)
    {
        var stub = new StubHttpHandler(_ => StubHttpHandler.Problem(status, code));

        var ex = await Assert.ThrowsAsync<DownstreamException>(() =>
            Client(stub).ChangeAsync(Token, CardAction.Unlock, "jane", null, CancellationToken.None));

        Assert.Equal(expected, ex.Failure);
        Assert.Equal("Unlock", ex.Operation);
    }

    private static CardPlatformClient Client(StubHttpHandler stub) =>
        new(stub.CreateClient("http://cards.test/"), NullLogger<CardPlatformClient>.Instance);
}
