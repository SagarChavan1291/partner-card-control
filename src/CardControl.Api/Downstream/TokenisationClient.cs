using System.Net;
using System.Net.Http.Json;
using CardControl.Api.Security;

namespace CardControl.Api.Downstream;

/// <summary>The outcome of exchanging a PAN: a card token, or the tokeniser's verdict that the PAN is invalid.</summary>
public sealed record TokenisationResult(string? CardToken)
{
    public static TokenisationResult InvalidPan { get; } = new((string?)null);

    public bool IsInvalidPan => CardToken is null;
}

public interface ITokenisationClient
{
    /// <exception cref="DownstreamException">The tokeniser is unreachable, slow, or answered off-contract.</exception>
    Task<TokenisationResult> TokeniseAsync(Pan pan, CancellationToken cancellationToken);
}

/// <summary>
/// The only place a PAN leaves this API in clear text (inside TLS / the mesh's mTLS in production).
/// Nothing here logs request or response bodies.
/// </summary>
public sealed partial class TokenisationClient(HttpClient http, ILogger<TokenisationClient> logger) : ITokenisationClient
{
    private const string Service = DownstreamServices.Tokenisation;
    private const string Operation = "Tokenise";

    public async Task<TokenisationResult> TokeniseAsync(Pan pan, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "tokens")
        {
            Content = JsonContent.Create(new TokeniseRequest(pan.RevealDigits()), options: DownstreamHttp.Json),
        };

        using var response = await DownstreamHttp.SendAsync(http, request, Service, Operation, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            var body = await DownstreamHttp.ReadBodyAsync<TokeniseResponse>(response, Service, Operation, cancellationToken);
            if (!CardTokens.IsWellFormed(body.CardToken))
                throw DownstreamHttp.BadResponse(response, Service, Operation);

            return new TokenisationResult(body.CardToken);
        }

        var code = await DownstreamHttp.ReadErrorCodeAsync(response, cancellationToken);
        LogError(logger, (int)response.StatusCode, code);

        return response.StatusCode == HttpStatusCode.BadRequest && code == "InvalidPan"
            ? TokenisationResult.InvalidPan
            : throw DownstreamHttp.Unexpected(response, code, Service, Operation);
    }

    private sealed record TokeniseRequest(string Pan);

    private sealed record TokeniseResponse(string? CardToken);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tokenisation service returned {StatusCode} {ErrorCode}")]
    private static partial void LogError(ILogger logger, int statusCode, string? errorCode);
}

internal static class CardTokens
{
    /// <summary>
    /// The token goes into card platform URLs, so only accept a conservative shape: never trust a
    /// downstream value to be safe in a path.
    /// </summary>
    public static bool IsWellFormed(string? token) =>
        token is { Length: > 0 and <= 128 } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
}
