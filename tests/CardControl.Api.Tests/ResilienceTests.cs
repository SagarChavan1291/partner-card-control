using System.Diagnostics;
using System.Net;
using CardControl.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace CardControl.Api.Tests;

/// <summary>
/// The downstream resilience pipeline: retries only what is safe to repeat, bounded timeouts, and a circuit
/// breaker. Uses short timeouts and delays so the tests stay fast; the production values are in appsettings.
/// </summary>
public sealed class ResilienceTests : IClassFixture<ResilienceTests.FastResilienceFactory>
{
    private const string CardPath = "/cards/ctok_8f3a1c2e9b7d4a01";

    private readonly FastResilienceFactory _api;
    private readonly HttpClient _client;

    public ResilienceTests(FastResilienceFactory api)
    {
        _api = api;
        _api.Bank.Reset();
        _client = api.CreateClient(api.Tokens.CreateWithWriteRole());
    }

    // ---- Retried: tokenise and get card ----

    [Fact]
    public async Task Transient_503_on_get_card_is_retried_and_the_lock_goes_through()
    {
        var failures = 1;
        _api.Bank.Fault = (req, _) => Task.FromResult(
            req.Method == HttpMethod.Get && Interlocked.Decrement(ref failures) >= 0
                ? FakeBank.Problem(HttpStatusCode.ServiceUnavailable, "ServiceUnavailable")
                : null);

        var response = await Lock("4111111111111111");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, Gets(CardPath));
        Assert.Contains(_api.Logs.Entries, e => e.Contains("CardPlatform: retry 1") && e.Contains("after HTTP 503"));
    }

    [Fact]
    public async Task Polly_logs_nothing_itself_because_it_would_log_exception_messages()
    {
        _api.Bank.Fault = (req, _) => req.RequestUri!.Host == "tokenisation.test"
            ? throw new HttpRequestException("Connection refused")
            : Task.FromResult<HttpResponseMessage?>(null);

        await Lock("4111111111111111");

        Assert.DoesNotContain(_api.Logs.Entries, e => e.Contains(" Polly:"));
        Assert.Contains(_api.Logs.Entries, e => e.Contains("Tokenisation: retry 1") && e.Contains("after HttpRequestException"));
    }

    [Fact]
    public async Task Transient_connection_failure_on_tokenise_is_retried()
    {
        var failures = 1;
        _api.Bank.Fault = (req, _) => req.RequestUri!.Host == "tokenisation.test" && Interlocked.Decrement(ref failures) >= 0
            ? throw new HttpRequestException("Connection refused")
            : Task.FromResult<HttpResponseMessage?>(null);

        var response = await Lock("4111111111111111");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, _api.Bank.RequestsTo("/tokens").Count());
    }

    [Fact]
    public async Task Slow_first_attempt_on_get_card_is_abandoned_and_retried()
    {
        var slow = 1;
        _api.Bank.Fault = async (req, ct) =>
        {
            if (req.Method == HttpMethod.Get && Interlocked.Decrement(ref slow) >= 0)
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return null;
        };

        var response = await Lock("4111111111111111");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, Gets(CardPath));
    }

    [Fact]
    public async Task Persistent_503_on_get_card_gives_up_after_the_retry_budget()
    {
        _api.Bank.Fault = (req, _) => Task.FromResult(req.Method == HttpMethod.Get
            ? FakeBank.Problem(HttpStatusCode.ServiceUnavailable, "ServiceUnavailable")
            : null);

        var response = await Lock("4111111111111111");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1 + FastResilienceFactory.MaxRetries, Gets(CardPath));
        Assert.Empty(_api.Bank.RequestsTo(CardPath + "/lock"));
    }

    [Fact]
    public async Task Business_errors_are_not_retried()
    {
        // 404 CardNotFound is an answer, not a fault.
        var response = await Lock("5555555555554444");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Single(_api.Bank.Requests, r => r.Method == HttpMethod.Get);
    }

    // ---- Never retried: lock / unlock ----

    [Fact]
    public async Task Lock_that_gets_a_503_is_sent_exactly_once()
    {
        _api.Bank.Fault = (req, _) => Task.FromResult(req.RequestUri!.AbsolutePath.EndsWith("/lock")
            ? FakeBank.Problem(HttpStatusCode.ServiceUnavailable, "ServiceUnavailable")
            : null);

        var response = await Lock("4111111111111111");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Single(_api.Bank.RequestsTo(CardPath + "/lock"));
    }

    [Fact]
    public async Task Unlock_that_fails_to_connect_is_sent_exactly_once()
    {
        _api.Bank.Fault = (req, _) => req.RequestUri!.AbsolutePath.EndsWith("/unlock")
            ? throw new HttpRequestException("Connection reset")
            : Task.FromResult<HttpResponseMessage?>(null);

        var response = await _client.SendAsync(ApiRequests.Change("/v1/cards/unlock",
            _api.EncryptPan("4242424242424242"), "CORP-1001"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Single(_api.Bank.RequestsTo("/cards/ctok_2b9e7f4c1a6d3e02/unlock"));
    }

    [Fact]
    public async Task Slow_lock_times_out_as_504_and_is_not_retried()
    {
        _api.Bank.Fault = async (req, ct) =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/lock"))
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return null;
        };

        var stopwatch = Stopwatch.StartNew();
        var response = await Lock("4111111111111111");

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal("GatewayTimeout", (await response.Json()).GetProperty("code").GetString());
        Assert.Single(_api.Bank.RequestsTo(CardPath + "/lock"));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"took {stopwatch.Elapsed}");
    }

    // ---- Timeouts ----

    [Fact]
    public async Task Card_platform_that_never_answers_is_504_within_the_total_timeout()
    {
        _api.Bank.Fault = async (req, ct) =>
        {
            if (req.RequestUri!.Host == "cards.test")
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return null;
        };

        var stopwatch = Stopwatch.StartNew();
        var response = await Lock("4111111111111111");

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(FastResilienceFactory.TotalTimeoutSeconds + 1.5),
            $"took {stopwatch.Elapsed}");
    }

    private Task<HttpResponseMessage> Lock(string pan) =>
        _client.SendAsync(ApiRequests.Change("/v1/cards/lock", _api.EncryptPan(pan), "CORP-1001"));

    private int Gets(string path) => _api.Bank.RequestsTo(path).Count(r => r.Method == HttpMethod.Get);

    /// <summary>Short timeouts and delays; breaker effectively off so tests here can't trip it for each other.</summary>
    public sealed class FastResilienceFactory : CardControlApiFactory
    {
        public const int MaxRetries = 2;
        public const double TotalTimeoutSeconds = 2;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(
                ResilienceSettings(attemptTimeout: 0.4, totalTimeout: TotalTimeoutSeconds, retries: MaxRetries,
                    breakerMinimumThroughput: 10_000)));
        }
    }

    public static Dictionary<string, string?> ResilienceSettings(double attemptTimeout, double totalTimeout, int retries,
        int breakerMinimumThroughput)
    {
        var settings = new Dictionary<string, string?>();
        foreach (var service in new[] { "Tokenisation", "CardPlatform" })
        {
            var prefix = $"Downstream:{service}:";
            settings[prefix + "AttemptTimeoutSeconds"] = attemptTimeout.ToString(System.Globalization.CultureInfo.InvariantCulture);
            settings[prefix + "TimeoutSeconds"] = Math.Ceiling(totalTimeout).ToString(System.Globalization.CultureInfo.InvariantCulture);
            settings[prefix + "MaxRetryAttempts"] = retries.ToString();
            settings[prefix + "RetryBaseDelayMilliseconds"] = "10";
            settings[prefix + "CircuitBreakerMinimumThroughput"] = breakerMinimumThroughput.ToString();
            settings[prefix + "CircuitBreakerFailureRatio"] = "0.5";
            settings[prefix + "CircuitBreakerBreakSeconds"] = "60";
        }
        return settings;
    }
}

/// <summary>Its own host, because breaker state is per host and would otherwise leak between tests.</summary>
public sealed class CircuitBreakerTests : IClassFixture<CircuitBreakerTests.BreakerFactory>
{
    private readonly BreakerFactory _api;

    public CircuitBreakerTests(BreakerFactory api) => _api = api;

    [Fact]
    public async Task Repeated_card_platform_failures_open_the_circuit_and_later_calls_fail_fast()
    {
        _api.Bank.Reset();
        _api.Bank.Fault = (req, _) => Task.FromResult(req.RequestUri!.Host == "cards.test"
            ? FakeBank.Problem(HttpStatusCode.ServiceUnavailable, "ServiceUnavailable")
            : null);
        var client = _api.CreateClient(_api.Tokens.CreateWithWriteRole());

        // Enough failed calls to cross the breaker's minimum throughput.
        for (var i = 0; i < 4; i++)
            await Lock(client);
        var callsBefore = CardPlatformCalls();

        var response = await Lock(client);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("ServiceUnavailable", (await response.Json()).GetProperty("code").GetString());
        Assert.Equal(callsBefore, CardPlatformCalls());   // failed fast: the card platform wasn't called
        Assert.Contains(_api.Logs.Entries, e => e.Contains("CircuitOpen"));
        Assert.Contains(_api.Logs.Entries, e => e.Contains("CardPlatform: circuit opened") && e.Contains("after HTTP 503"));
    }

    private Task<HttpResponseMessage> Lock(HttpClient client) =>
        client.SendAsync(ApiRequests.Change("/v1/cards/lock", _api.EncryptPan("4111111111111111"), "CORP-1001"));

    private int CardPlatformCalls() => _api.Bank.Requests.Count(r => r.Uri.Host == "cards.test");

    public sealed class BreakerFactory : CardControlApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(
                ResilienceTests.ResilienceSettings(attemptTimeout: 0.4, totalTimeout: 2, retries: 0, breakerMinimumThroughput: 3)));
        }
    }
}
