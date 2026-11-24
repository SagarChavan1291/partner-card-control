using CardControl.Api.Downstream;
using Microsoft.AspNetCore.Diagnostics;

namespace CardControl.Api.Errors;

/// <summary>
/// Maps downstream infrastructure failures to gateway-style responses. The partner learns that the bank
/// couldn't complete the request and whether retrying makes sense, not which internal service failed or why.
/// </summary>
internal sealed partial class DownstreamExceptionHandler(
    IProblemDetailsService problemDetails,
    ILogger<DownstreamExceptionHandler> logger) : IExceptionHandler
{
    private const int RetryAfterSeconds = 5;

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not DownstreamException downstream)
            return false;

        LogFailure(logger, downstream.Service, downstream.Operation, downstream.Failure, downstream.StatusCode, downstream.ErrorCode);

        var (status, code, title) = downstream.Failure switch
        {
            DownstreamFailure.Unavailable => (StatusCodes.Status503ServiceUnavailable, ErrorCodes.ServiceUnavailable,
                "A service needed for this request is unavailable. Try again shortly."),
            DownstreamFailure.Timeout => (StatusCodes.Status504GatewayTimeout, ErrorCodes.GatewayTimeout,
                "A service needed for this request did not respond in time. The card's state may or may not have changed."),
            _ => (StatusCodes.Status502BadGateway, ErrorCodes.BadGateway,
                "A service needed for this request returned an unexpected response."),
        };

        if (status == StatusCodes.Status503ServiceUnavailable)
            httpContext.Response.Headers.RetryAfter = RetryAfterSeconds.ToString();

        httpContext.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails =
            {
                Status = status,
                Title = title,
                Extensions = { ["code"] = code },
            },
        });
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Downstream failure: {Service} {Operation} {Failure} (status {StatusCode}, code {ErrorCode})")]
    private static partial void LogFailure(ILogger logger, string service, string operation, DownstreamFailure failure,
        int? statusCode, string? errorCode);
}
