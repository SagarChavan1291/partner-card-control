using System.Net;
using System.Text.Json;
using CardControl.Api.Downstream;
using CardControl.Api.Security;
using CardControl.Api.Tests.Infrastructure;
using Microsoft.Extensions.Logging;

namespace CardControl.Api.Tests.Downstream;

public sealed class TokenisationClientTests : IDisposable
{
    private const string Digits = "4111111111111111";
    private static readonly Pan TestPan = Pan.TryCreate(Digits, out var p) ? p! : throw new InvalidOperationException();

    private readonly CapturingLoggerProvider _logs = new();
    private readonly ILoggerFactory _loggerFactory;

    public TokenisationClientTests() => _loggerFactory = LoggerFactory.Create(b => b.AddProvider(_logs).SetMinimumLevel(LogLevel.Trace));

    public void Dispose() => _loggerFactory.Dispose();

    [Fact]
    public async Task Posts_the_pan_to_tokens_and_returns_the_card_token()
    {
        var stub = new StubHttpHandler(_ => StubHttpHandler.Json(HttpStatusCode.OK, """{"cardToken":"ctok_8f3a1c2e9b7d4a01"}"""));

        var result = await Client(stub).TokeniseAsync(TestPan, CancellationToken.None);

        Assert.Equal("ctok_8f3a1c2e9b7d4a01", result.CardToken);
        Assert.False(result.IsInvalidPan);
        var sent = Assert.Single(stub.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("http://tokeniser.test/tokens", sent.Uri.ToString());
        Assert.Equal(Digits, JsonDocument.Parse(sent.Body!).RootElement.GetProperty("pan").GetString());
    }

    [Fact]
    public async Task Invalid_pan_verdict_is_a_result_not_an_exception()
    {
        var stub = new StubHttpHandler(_ => StubHttpHandler.Problem(HttpStatusCode.BadRequest, "InvalidPan"));

        var result = await Client(stub).TokeniseAsync(TestPan, CancellationToken.None);

        Assert.True(result.IsInvalidPan);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, DownstreamFailure.Unavailable)]
    [InlineData(HttpStatusCode.InternalServerError, DownstreamFailure.Unavailable)]
    [InlineData(HttpStatusCode.NotFound, DownstreamFailure.BadResponse)]
    [InlineData(HttpStatusCode.Unauthorized, DownstreamFailure.BadResponse)]
    public async Task Other_errors_are_downstream_failures(HttpStatusCode status, DownstreamFailure expected)
    {
        var stub = new StubHttpHandler(_ => StubHttpHandler.Problem(status, "Whatever"));

        var ex = await Assert.ThrowsAsync<DownstreamException>(() => Client(stub).TokeniseAsync(TestPan, CancellationToken.None));

        Assert.Equal(expected, ex.Failure);
        Assert.Equal((int)status, ex.StatusCode);
        Assert.Equal(DownstreamServices.Tokenisation, ex.Service);
    }

    [Fact]
    public async Task A_400_with_another_code_is_not_mistaken_for_invalid_pan()
    {
        var stub = new StubHttpHandler(_ => StubHttpHandler.Problem(HttpStatusCode.BadRequest, "SomethingElse"));

        var ex = await Assert.ThrowsAsync<DownstreamException>(() => Client(stub).TokeniseAsync(TestPan, CancellationToken.None));

        Assert.Equal(DownstreamFailure.BadResponse, ex.Failure);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"cardToken":null}""")]
    [InlineData("""{"cardToken":""}""")]
    [InlineData("""{"cardToken":"../admin/reset"}""")]       // would escape the card platform's /cards/ path
    [InlineData("""{"cardToken":"ctok_1?x=1"}""")]
    [InlineData("""not json""")]
    [InlineData("""[]""")]
    public async Task Off_contract_success_bodies_are_bad_responses(string body)
    {
        var stub = new StubHttpHandler(_ => StubHttpHandler.Json(HttpStatusCode.OK, body));

        var ex = await Assert.ThrowsAsync<DownstreamException>(() => Client(stub).TokeniseAsync(TestPan, CancellationToken.None));

        Assert.Equal(DownstreamFailure.BadResponse, ex.Failure);
    }

    [Fact]
    public async Task Connection_failure_is_unavailable()
    {
        var stub = new StubHttpHandler(_ => throw new HttpRequestException("Connection refused (tokeniser.test:80)"));

        var ex = await Assert.ThrowsAsync<DownstreamException>(() => Client(stub).TokeniseAsync(TestPan, CancellationToken.None));

        Assert.Equal(DownstreamFailure.Unavailable, ex.Failure);
    }

    [Fact]
    public async Task Exceeding_the_client_timeout_is_a_timeout()
    {
        var stub = new StubHttpHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return StubHttpHandler.Json(HttpStatusCode.OK, """{"cardToken":"ctok_x"}""");
        });
        var http = stub.CreateClient("http://tokeniser.test/");
        http.Timeout = TimeSpan.FromMilliseconds(100);

        var ex = await Assert.ThrowsAsync<DownstreamException>(() =>
            new TokenisationClient(http, _loggerFactory.CreateLogger<TokenisationClient>()).TokeniseAsync(TestPan, CancellationToken.None));

        Assert.Equal(DownstreamFailure.Timeout, ex.Failure);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_reported_as_a_downstream_failure()
    {
        using var cts = new CancellationTokenSource();
        var stub = new StubHttpHandler(async (_, _) =>
        {
            await cts.CancelAsync();
            cts.Token.ThrowIfCancellationRequested();
            return StubHttpHandler.Json(HttpStatusCode.OK, "{}");
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(stub).TokeniseAsync(TestPan, cts.Token));
    }

    [Fact]
    public async Task The_pan_never_appears_in_logs_or_exceptions()
    {
        var stub = new StubHttpHandler(_ => StubHttpHandler.Problem(HttpStatusCode.ServiceUnavailable, "ServiceUnavailable"));

        var ex = await Assert.ThrowsAsync<DownstreamException>(() => Client(stub).TokeniseAsync(TestPan, CancellationToken.None));

        Assert.DoesNotContain(Digits, ex.ToString());
        Assert.NotEmpty(_logs.Entries);
        Assert.DoesNotContain(Digits, _logs.AllText);
    }

    private TokenisationClient Client(StubHttpHandler stub) =>
        new(stub.CreateClient("http://tokeniser.test/"), _loggerFactory.CreateLogger<TokenisationClient>());
}
