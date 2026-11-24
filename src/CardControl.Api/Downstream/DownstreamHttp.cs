using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace CardControl.Api.Downstream;

/// <summary>Shared plumbing for the downstream clients: sending, timeouts, and reading RFC 9457 errors.</summary>
internal static class DownstreamHttp
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        // Fail closed on off-contract bodies: missing members and nulls in non-nullable members are errors.
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    /// <summary>
    /// Sends the request, turning transport failures into <see cref="DownstreamException"/>.
    /// Cancellation by the caller (the partner hung up) is left as an OperationCanceledException.
    /// </summary>
    public static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpRequestMessage request,
        string service, string operation, CancellationToken cancellationToken)
    {
        try
        {
            return await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutRejectedException)
        {
            // The resilience pipeline's attempt or total timeout fired.
            throw new DownstreamException(service, operation, DownstreamFailure.Timeout);
        }
        catch (OperationCanceledException)
        {
            // HttpClient.Timeout (the backstop) fired; the caller didn't cancel.
            throw new DownstreamException(service, operation, DownstreamFailure.Timeout);
        }
        catch (BrokenCircuitException)
        {
            // Circuit open: failing fast without calling the service.
            throw new DownstreamException(service, operation, DownstreamFailure.Unavailable, errorCode: "CircuitOpen");
        }
        catch (ExecutionRejectedException)
        {
            // Anything else the pipeline refused to run (e.g. the concurrency limiter).
            throw new DownstreamException(service, operation, DownstreamFailure.Unavailable);
        }
        catch (HttpRequestException ex)
        {
            // Connection refused, DNS, TLS... Don't keep the inner exception: its message can contain the URL.
            throw new DownstreamException(service, operation, DownstreamFailure.Unavailable, (int?)ex.StatusCode);
        }
    }

    /// <summary>Reads a success body, treating anything unparseable or missing as a contract violation.</summary>
    public static async Task<T> ReadBodyAsync<T>(HttpResponseMessage response, string service, string operation,
        CancellationToken cancellationToken) where T : class
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken)
                ?? throw BadResponse(response, service, operation);
        }
        catch (JsonException)
        {
            throw BadResponse(response, service, operation);
        }
        catch (NotSupportedException)   // wrong content type
        {
            throw BadResponse(response, service, operation);
        }
    }

    /// <summary>The machine-readable <c>code</c> from a Problem Details body, if there is one.</summary>
    public static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.String
                    ? code.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Errors that aren't business outcomes: 5xx means unavailable, anything else breaks the contract.</summary>
    public static DownstreamException Unexpected(HttpResponseMessage response, string? errorCode, string service, string operation) =>
        new(service, operation,
            (int)response.StatusCode >= 500 ? DownstreamFailure.Unavailable : DownstreamFailure.BadResponse,
            (int)response.StatusCode, errorCode);

    public static DownstreamException BadResponse(HttpResponseMessage response, string service, string operation) =>
        new(service, operation, DownstreamFailure.BadResponse, (int)response.StatusCode);
}
