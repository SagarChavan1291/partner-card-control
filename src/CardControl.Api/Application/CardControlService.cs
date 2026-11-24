using CardControl.Api.Downstream;
using CardControl.Api.Security;

namespace CardControl.Api.Application;

/// <summary>A validated lock/unlock request, with the caller's identity taken from its access token.</summary>
public sealed record CardChangeCommand(
    CardAction Action,
    string EncryptedPan,
    string CorporateId,
    string InitiatingUser,
    string ClientId);

public enum CardControlError
{
    CorporateNotEnabled,
    InvalidEncryptedPan,
    InvalidPan,
    /// <summary>Unknown card, or a card the partner isn't entitled to: deliberately indistinguishable.</summary>
    CardNotFound,
    CardAlreadyLocked,
    CardNotLocked,
    CardCancelled,
}

public sealed record CardControlResult(CardDetails? Card, CardControlError? Error)
{
    public static CardControlResult Success(CardDetails card) => new(card, null);
    public static CardControlResult Failure(CardControlError error) => new(null, error);
}

/// <summary>
/// The lock/unlock flow: entitlement, PAN decryption, tokenisation, ownership check, then the change.
/// Knows nothing about HTTP; the endpoint maps results to responses.
/// Infrastructure failures (downstream unavailable, timeouts) propagate as <see cref="DownstreamException"/>.
/// </summary>
public sealed partial class CardControlService(
    IEntitlementPolicy entitlements,
    IPanDecryptor decryptor,
    ITokenisationClient tokenisation,
    ICardPlatformClient cardPlatform,
    ILogger<CardControlService> logger)
{
    public async Task<CardControlResult> ChangeAsync(CardChangeCommand command, CancellationToken cancellationToken)
    {
        // 1. Cheapest check first, and before touching the PAN: is this partner enabled for this corporate at all?
        if (!entitlements.IsCorporateEnabled(command.ClientId, command.CorporateId))
        {
            LogCorporateNotEnabled(logger, command.Action, command.ClientId, command.CorporateId);
            return CardControlResult.Failure(CardControlError.CorporateNotEnabled);
        }

        // 2. Decrypt. Every cryptographic failure looks the same to the caller; the reason is logged.
        Pan pan;
        try
        {
            pan = decryptor.Decrypt(command.EncryptedPan);
        }
        catch (PanDecryptionException ex)
        {
            LogDecryptionFailed(logger, command.Action, command.ClientId, ex.Reason);
            return CardControlResult.Failure(ex.Reason == PanDecryptionFailure.InvalidPan
                ? CardControlError.InvalidPan
                : CardControlError.InvalidEncryptedPan);
        }

        // 3. PAN -> card token. From here on, only the token is used.
        var tokenised = await tokenisation.TokeniseAsync(pan, cancellationToken);
        if (tokenised.IsInvalidPan)
            return CardControlResult.Failure(CardControlError.InvalidPan);
        var cardToken = tokenised.CardToken!;

        // 4. Ownership: the card must belong to the corporate the partner named. A card with no corporate
        //    belongs to nobody the partner can act for. Not found and not yours get the same answer.
        var card = await cardPlatform.GetCardAsync(cardToken, cancellationToken);
        if (card is null)
        {
            LogCardNotFound(logger, command.Action, command.ClientId, pan.LastFour);
            return CardControlResult.Failure(CardControlError.CardNotFound);
        }
        if (!string.Equals(card.CorporateId, command.CorporateId, StringComparison.Ordinal))
        {
            LogOwnershipMismatch(logger, command.Action, command.ClientId, cardToken, command.CorporateId, card.CorporateId);
            return CardControlResult.Failure(CardControlError.CardNotFound);
        }

        // 5. The change. No pre-check of the card's status: the card platform decides, and records the
        //    attempt (successful or not) against the initiating user in its audit trail.
        var change = await cardPlatform.ChangeAsync(cardToken, command.Action, command.InitiatingUser,
            channel: command.ClientId, cancellationToken);

        LogChangeOutcome(logger, command.Action, change.Outcome, command.ClientId, command.CorporateId, cardToken, command.InitiatingUser);

        return change.Outcome switch
        {
            CardChangeOutcome.Changed => CardControlResult.Success(change.Card!),
            CardChangeOutcome.CardNotFound => CardControlResult.Failure(CardControlError.CardNotFound),
            CardChangeOutcome.AlreadyLocked => CardControlResult.Failure(CardControlError.CardAlreadyLocked),
            CardChangeOutcome.NotLocked => CardControlResult.Failure(CardControlError.CardNotLocked),
            CardChangeOutcome.Cancelled => CardControlResult.Failure(CardControlError.CardCancelled),
            _ => throw new InvalidOperationException($"Unhandled outcome {change.Outcome}."),
        };
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Action} refused: client {ClientId} is not enabled for corporate {CorporateId}")]
    private static partial void LogCorporateNotEnabled(ILogger logger, CardAction action, string clientId, string corporateId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Action} refused: encrypted PAN from client {ClientId} rejected ({Reason})")]
    private static partial void LogDecryptionFailed(ILogger logger, CardAction action, string clientId, PanDecryptionFailure reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Action} refused: no card for PAN ending {LastFour} (client {ClientId})")]
    private static partial void LogCardNotFound(ILogger logger, CardAction action, string clientId, string lastFour);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "{Action} refused: client {ClientId} named corporate {RequestedCorporateId} but card {CardToken} belongs to {ActualCorporateId}")]
    private static partial void LogOwnershipMismatch(ILogger logger, CardAction action, string clientId, string cardToken,
        string requestedCorporateId, string? actualCorporateId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "{Action} {Outcome}: card {CardToken}, corporate {CorporateId}, client {ClientId}, initiated by {InitiatingUser}")]
    private static partial void LogChangeOutcome(ILogger logger, CardAction action, CardChangeOutcome outcome, string clientId,
        string corporateId, string cardToken, string initiatingUser);
}
