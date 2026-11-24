namespace CardControl.Api.Downstream;

public enum DownstreamFailure
{
    /// <summary>Couldn't reach it, or it said it's unavailable (5xx). Worth the partner retrying later.</summary>
    Unavailable,
    /// <summary>No answer within the configured timeout.</summary>
    Timeout,
    /// <summary>It answered, but not in a way the contract allows: unexpected status, code or body.</summary>
    BadResponse,
}

/// <summary>
/// An infrastructure-level failure talking to a downstream service. Business outcomes (card not found,
/// already locked...) are not exceptions; they're returned as results. The message is built only from
/// the service name, operation, status and error code: never from request or response bodies.
/// </summary>
public sealed class DownstreamException : Exception
{
    public DownstreamException(string service, string operation, DownstreamFailure failure,
        int? statusCode = null, string? errorCode = null, Exception? innerException = null)
        : base(BuildMessage(service, operation, failure, statusCode, errorCode), innerException)
    {
        Service = service;
        Operation = operation;
        Failure = failure;
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }

    public string Service { get; }
    public string Operation { get; }
    public DownstreamFailure Failure { get; }
    public int? StatusCode { get; }
    public string? ErrorCode { get; }

    private static string BuildMessage(string service, string operation, DownstreamFailure failure, int? status, string? code) =>
        $"{service} {operation} failed: {failure}" +
        (status is null ? "" : $" (HTTP {status}{(code is null ? "" : $", {code}")})");
}
