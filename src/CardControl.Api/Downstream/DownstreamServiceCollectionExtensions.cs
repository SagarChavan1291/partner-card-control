using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;

namespace CardControl.Api.Downstream;

public static class DownstreamServiceCollectionExtensions
{
    public const string ResilienceLogCategory = "CardControl.Api.Downstream.Resilience";

    public static IServiceCollection AddDownstreamClients(this IServiceCollection services, IConfiguration configuration)
    {
        AddOptions(services, configuration, DownstreamServices.Tokenisation);
        AddOptions(services, configuration, DownstreamServices.CardPlatform);

        // Tokenisation: POST /tokens is idempotent in effect (same PAN, same token), so it's retried.
        services.AddHttpClient<ITokenisationClient, TokenisationClient>((sp, http) =>
                Configure(http, sp, DownstreamServices.Tokenisation))
            .AddStandardResilienceHandler()
            .Configure((resilience, sp) => ConfigureResilience(resilience, sp, DownstreamServices.Tokenisation, retryUnsafeMethods: true));

        // Card platform: GET is retried; lock/unlock (POST) are not. If a lock reached the platform but the
        // response was lost, a retry would get 409 CardAlreadyLocked and add a second audit entry.
        services.AddHttpClient<ICardPlatformClient, CardPlatformClient>((sp, http) =>
                Configure(http, sp, DownstreamServices.CardPlatform))
            .AddStandardResilienceHandler()
            .Configure((resilience, sp) => ConfigureResilience(resilience, sp, DownstreamServices.CardPlatform, retryUnsafeMethods: false));

        return services;
    }

    private static void AddOptions(IServiceCollection services, IConfiguration configuration, string name) =>
        services.AddOptions<DownstreamServiceOptions>(name)
            .Bind(configuration.GetSection($"{DownstreamServices.SectionName}:{name}"))
            .ValidateDataAnnotations()
            .ValidateOnStart();

    /// <summary>Status code or exception type only: exception messages can carry request data.</summary>
    private static string Describe(Outcome<HttpResponseMessage> outcome) =>
        outcome.Exception is { } ex ? ex.GetType().Name : $"HTTP {(int?)outcome.Result?.StatusCode}";

    private static DownstreamServiceOptions Get(IServiceProvider sp, string name) =>
        sp.GetRequiredService<IOptionsMonitor<DownstreamServiceOptions>>().Get(name);

    private static void Configure(HttpClient http, IServiceProvider sp, string name)
    {
        var options = Get(sp, name);
        // Trailing slash so relative paths ("tokens", "cards/...") append rather than replace the last segment.
        http.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
        // The resilience pipeline owns timeouts; this is only a backstop.
        http.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds + 5);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    /// <summary>
    /// The standard pipeline, outermost first: rate limiter, total timeout, retry, circuit breaker, attempt timeout.
    /// Retries handle 5xx, 408, 429, connection failures and attempt timeouts, with exponential backoff and jitter.
    /// </summary>
    private static void ConfigureResilience(HttpStandardResilienceOptions resilience, IServiceProvider sp, string name,
        bool retryUnsafeMethods)
    {
        var options = Get(sp, name);

        resilience.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        resilience.AttemptTimeout.Timeout = TimeSpan.FromSeconds(options.AttemptTimeoutSeconds);

        resilience.Retry.Delay = TimeSpan.FromMilliseconds(options.RetryBaseDelayMilliseconds);
        resilience.Retry.BackoffType = DelayBackoffType.Exponential;
        resilience.Retry.UseJitter = true;
        if (options.MaxRetryAttempts == 0)
            resilience.Retry.ShouldHandle = _ => ValueTask.FromResult(false);   // Polly needs MaxRetryAttempts >= 1
        else
            resilience.Retry.MaxRetryAttempts = options.MaxRetryAttempts;
        if (!retryUnsafeMethods)
            resilience.Retry.DisableForUnsafeHttpMethods();

        // Polly's own logging is switched off (Program.cs) because it logs exception messages; these
        // callbacks log the same events with only the outcome's status code or exception type.
        var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger(ResilienceLogCategory);
        resilience.Retry.OnRetry = args =>
        {
            ResilienceLog.Retrying(log, name, args.AttemptNumber + 1, args.RetryDelay.TotalMilliseconds, Describe(args.Outcome));
            return default;
        };
        resilience.CircuitBreaker.OnOpened = args =>
        {
            ResilienceLog.CircuitOpened(log, name, args.BreakDuration.TotalSeconds, Describe(args.Outcome));
            return default;
        };
        resilience.CircuitBreaker.OnClosed = _ =>
        {
            ResilienceLog.CircuitClosed(log, name);
            return default;
        };

        resilience.CircuitBreaker.FailureRatio = options.CircuitBreakerFailureRatio;
        resilience.CircuitBreaker.MinimumThroughput = options.CircuitBreakerMinimumThroughput;
        resilience.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(options.CircuitBreakerBreakSeconds);
        // The sampling window must be at least twice the attempt timeout (the library validates this).
        resilience.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(Math.Max(30, options.AttemptTimeoutSeconds * 2));
    }
}

internal static partial class ResilienceLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "{Service}: retry {Attempt} in {DelayMs}ms after {Outcome}")]
    public static partial void Retrying(ILogger logger, string service, int attempt, double delayMs, string outcome);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Service}: circuit opened for {BreakSeconds}s after {Outcome}")]
    public static partial void CircuitOpened(ILogger logger, string service, double breakSeconds, string outcome);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Service}: circuit closed")]
    public static partial void CircuitClosed(ILogger logger, string service);
}
