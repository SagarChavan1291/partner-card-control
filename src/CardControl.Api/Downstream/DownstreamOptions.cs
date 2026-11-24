using System.ComponentModel.DataAnnotations;

namespace CardControl.Api.Downstream;

public sealed class DownstreamServiceOptions : IValidatableObject
{
    [Required, Url]
    public string BaseUrl { get; set; } = "";

    /// <summary>Total time for one logical call, retries included. Past this the partner gets a 504.</summary>
    [Range(1, 60)]
    public int TimeoutSeconds { get; set; } = 5;

    /// <summary>Time for a single attempt. A slow attempt is abandoned (and retried, if the call is retryable).</summary>
    [Range(0.1, 30)]
    public double AttemptTimeoutSeconds { get; set; } = 2;

    /// <summary>Retries for calls that are safe to repeat (tokenise, get card). Lock/unlock are never retried.</summary>
    [Range(0, 5)]
    public int MaxRetryAttempts { get; set; } = 2;

    /// <summary>First retry delay; doubles each time, with jitter.</summary>
    [Range(1, 10_000)]
    public int RetryBaseDelayMilliseconds { get; set; } = 200;

    /// <summary>Open the circuit when at least this share of calls fail...</summary>
    [Range(0.01, 1.0)]
    public double CircuitBreakerFailureRatio { get; set; } = 0.5;

    /// <summary>...over at least this many calls in the sampling window...</summary>
    [Range(2, 10_000)]
    public int CircuitBreakerMinimumThroughput { get; set; } = 20;

    /// <summary>...then fail fast for this long before letting a trial call through.</summary>
    [Range(1, 300)]
    public int CircuitBreakerBreakSeconds { get; set; } = 15;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (AttemptTimeoutSeconds > TimeoutSeconds)
            yield return new ValidationResult("AttemptTimeoutSeconds must not exceed TimeoutSeconds.",
                [nameof(AttemptTimeoutSeconds), nameof(TimeoutSeconds)]);
    }
}

public static class DownstreamServices
{
    public const string SectionName = "Downstream";
    public const string Tokenisation = nameof(Tokenisation);
    public const string CardPlatform = nameof(CardPlatform);
}
