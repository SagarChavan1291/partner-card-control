using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.HttpResults;

namespace CardControl.Api.Errors;

public static class ProblemDetailsSetup
{
    private const string GenericServerErrorDetail = "An unexpected error occurred. Quote the traceId if you contact support.";

    /// <summary>
    /// Every error leaves the API as RFC 9457 Problem Details with a <c>code</c> and a <c>traceId</c>.
    /// Server errors never carry exception text, which could contain sensitive request data.
    /// </summary>
    public static IServiceCollection AddApiProblemDetails(this IServiceCollection services)
    {
        // Exception handlers run in registration order; the catch-all goes last.
        services.AddExceptionHandler<DownstreamExceptionHandler>();
        services.AddExceptionHandler<UnhandledExceptionHandler>();
        services.AddProblemDetails(options => options.CustomizeProblemDetails = ctx =>
        {
            var problem = ctx.ProblemDetails;
            var status = problem.Status ?? ctx.HttpContext.Response.StatusCode;

            problem.Extensions.TryAdd("code", ErrorCodes.ForStatus(status));
            problem.Extensions["traceId"] = Activity.Current?.Id ?? ctx.HttpContext.TraceIdentifier;

            if (status >= StatusCodes.Status500InternalServerError && ctx.Exception is not null)
            {
                problem.Title = "Internal server error";
                problem.Detail = GenericServerErrorDetail;
                problem.Extensions.Remove("exception");
            }
        });
        return services;
    }

    /// <summary>Builds a Problem Details result with an explicit error code.</summary>
    public static ProblemHttpResult Problem(int status, string code, string title, string? detail = null) =>
        TypedResults.Problem(
            title: title,
            detail: detail,
            statusCode: status,
            extensions: new Dictionary<string, object?> { ["code"] = code });
}

/// <summary>
/// Logs unhandled exceptions without their message (only type and stack trace), so that request data
/// such as a PAN can't reach the logs via an exception message, then writes a generic 500.
/// </summary>
internal sealed partial class UnhandledExceptionHandler(
    IProblemDetailsService problemDetails,
    ILogger<UnhandledExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            // The client went away; nothing useful to send back.
            LogClientAborted(logger, httpContext.Request.Path);
            return true;
        }

        LogUnhandled(logger, exception.GetType().FullName, httpContext.Request.Method, httpContext.Request.Path, exception.StackTrace);

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = { Status = StatusCodes.Status500InternalServerError },
        });
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled {ExceptionType} on {Method} {Path}. Stack trace: {StackTrace}")]
    private static partial void LogUnhandled(ILogger logger, string? exceptionType, string method, PathString path, string? stackTrace);

    [LoggerMessage(Level = LogLevel.Information, Message = "Request to {Path} aborted by the client")]
    private static partial void LogClientAborted(ILogger logger, PathString path);
}
