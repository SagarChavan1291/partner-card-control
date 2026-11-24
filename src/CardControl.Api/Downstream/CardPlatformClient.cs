using System.Net;
using System.Net.Http.Json;

namespace CardControl.Api.Downstream;

public enum CardStatus { Active, Locked, Cancelled }

/// <summary>A card as the card platform describes it. <see cref="CorporateId"/> can be null.</summary>
public sealed record CardDetails(
    string CardToken,
    string? CorporateId,
    string CardholderName,
    string MaskedPan,
    CardStatus Status,
    DateTimeOffset UpdatedAt);

public enum CardAction { Lock, Unlock }

public enum CardChangeOutcome
{
    Changed,
    CardNotFound,
    AlreadyLocked,
    NotLocked,
    Cancelled,
}

/// <summary>Result of a lock or unlock. <see cref="Card"/> is set only when the change succeeded.</summary>
public sealed record CardChangeResult(CardChangeOutcome Outcome, CardDetails? Card = null);

public interface ICardPlatformClient
{
    /// <returns>The card, or null if the card platform doesn't know the token.</returns>
    /// <exception cref="DownstreamException">Unreachable, slow, or answered off-contract.</exception>
    Task<CardDetails?> GetCardAsync(string cardToken, CancellationToken cancellationToken);

    /// <summary>Locks or unlocks the card; the card platform records the attempt against <paramref name="initiatingUser"/>.</summary>
    /// <exception cref="DownstreamException">Unreachable, slow, or answered off-contract.</exception>
    Task<CardChangeResult> ChangeAsync(string cardToken, CardAction action, string initiatingUser, string? channel,
        CancellationToken cancellationToken);
}

public sealed partial class CardPlatformClient(HttpClient http, ILogger<CardPlatformClient> logger) : ICardPlatformClient
{
    private const string Service = DownstreamServices.CardPlatform;

    public async Task<CardDetails?> GetCardAsync(string cardToken, CancellationToken cancellationToken)
    {
        const string operation = "GetCard";
        using var request = new HttpRequestMessage(HttpMethod.Get, CardPath(cardToken));
        using var response = await DownstreamHttp.SendAsync(http, request, Service, operation, cancellationToken);

        if (response.IsSuccessStatusCode)
            return await ReadCardAsync(response, operation, cancellationToken);

        var code = await DownstreamHttp.ReadErrorCodeAsync(response, cancellationToken);
        LogError(logger, operation, (int)response.StatusCode, code);

        return response.StatusCode == HttpStatusCode.NotFound && code == "CardNotFound"
            ? null
            : throw DownstreamHttp.Unexpected(response, code, Service, operation);
    }

    public async Task<CardChangeResult> ChangeAsync(string cardToken, CardAction action, string initiatingUser,
        string? channel, CancellationToken cancellationToken)
    {
        var operation = action.ToString();
        var path = CardPath(cardToken) + (action == CardAction.Lock ? "/lock" : "/unlock");
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(new ChangeRequest(initiatingUser, channel), options: DownstreamHttp.Json),
        };
        using var response = await DownstreamHttp.SendAsync(http, request, Service, operation, cancellationToken);

        if (response.IsSuccessStatusCode)
            return new CardChangeResult(CardChangeOutcome.Changed, await ReadCardAsync(response, operation, cancellationToken));

        var code = await DownstreamHttp.ReadErrorCodeAsync(response, cancellationToken);
        LogError(logger, operation, (int)response.StatusCode, code);

        // Match on status AND code: a code under an unexpected status is treated as off-contract, not trusted.
        CardChangeOutcome? outcome = ((int)response.StatusCode, code) switch
        {
            (404, "CardNotFound") => CardChangeOutcome.CardNotFound,
            (409, "CardAlreadyLocked") => CardChangeOutcome.AlreadyLocked,
            (409, "CardNotLocked") => CardChangeOutcome.NotLocked,
            (422, "CardCancelled") => CardChangeOutcome.Cancelled,
            // 400 InvalidRequest would mean we sent a bad initiating user: our bug, so a contract failure.
            _ => null,
        };

        return outcome is { } known
            ? new CardChangeResult(known)
            : throw DownstreamHttp.Unexpected(response, code, Service, operation);
    }

    private static string CardPath(string cardToken)
    {
        if (!CardTokens.IsWellFormed(cardToken))
            throw new ArgumentException("Malformed card token.", nameof(cardToken));
        return "cards/" + Uri.EscapeDataString(cardToken);
    }

    private static async Task<CardDetails> ReadCardAsync(HttpResponseMessage response, string operation,
        CancellationToken cancellationToken)
    {
        var card = await DownstreamHttp.ReadBodyAsync<CardDetails>(response, Service, operation, cancellationToken);
        // Required fields: a card without its token or masked PAN isn't one we can reason about.
        if (string.IsNullOrEmpty(card.CardToken) || string.IsNullOrEmpty(card.MaskedPan) || !Enum.IsDefined(card.Status))
            throw DownstreamHttp.BadResponse(response, Service, operation);
        return card;
    }

    private sealed record ChangeRequest(string InitiatingUser, string? Channel);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Card platform {Operation} returned {StatusCode} {ErrorCode}")]
    private static partial void LogError(ILogger logger, string operation, int statusCode, string? errorCode);
}
