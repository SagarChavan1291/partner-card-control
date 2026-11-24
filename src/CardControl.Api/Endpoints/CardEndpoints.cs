using System.Security.Claims;
using CardControl.Api.Application;
using CardControl.Api.Downstream;
using CardControl.Api.Errors;
using CardControl.Api.Security;
using Microsoft.AspNetCore.Http.HttpResults;

namespace CardControl.Api.Endpoints;

/// <summary>Request body. The PAN only ever arrives encrypted, and never in the URL (URLs end up in logs).</summary>
/// <param name="EncryptedPan">Compact JWE: alg RSA-OAEP-256, enc A256GCM, encrypted with the bank's public key.</param>
/// <param name="CorporateId">The corporate the card belongs to, from the partner's records.</param>
public sealed record CardChangeRequest(string? EncryptedPan, string? CorporateId);

/// <summary>The card after the change. No card token or cardholder name: the partner doesn't need them.</summary>
public sealed record CardChangeResponse(
    string Action,
    string CorporateId,
    string MaskedPan,
    string Status,
    DateTimeOffset UpdatedAt);

public static class CardEndpoints
{
    public const string InitiatingUserHeader = "X-Initiating-User";
    public const int MaxInitiatingUserLength = 128;   // the card platform's limit
    public const int MaxCorporateIdLength = 64;

    public static IEndpointRouteBuilder MapCardEndpoints(this IEndpointRouteBuilder app)
    {
        var cards = app.MapGroup("/v1/cards")
            .RequireAuthorization(Policies.CardControlWrite)
            .WithTags("Cards")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .ProducesProblem(StatusCodes.Status504GatewayTimeout);

        cards.MapPost("/lock", (CardChangeRequest? body, HttpContext http, CardControlService service, CancellationToken ct) =>
                Handle(CardAction.Lock, body, http, service, ct))
            .WithName("LockCard")
            .WithSummary("Lock a card")
            .Accepts<CardChangeRequest>("application/json");

        cards.MapPost("/unlock", (CardChangeRequest? body, HttpContext http, CardControlService service, CancellationToken ct) =>
                Handle(CardAction.Unlock, body, http, service, ct))
            .WithName("UnlockCard")
            .WithSummary("Unlock a card")
            .Accepts<CardChangeRequest>("application/json");

        return app;
    }

    private static async Task<Results<Ok<CardChangeResponse>, ProblemHttpResult, ValidationProblem>> Handle(
        CardAction action, CardChangeRequest? body, HttpContext http, CardControlService service, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();

        var initiatingUser = ReadInitiatingUser(http.Request, errors);

        if (body is null)
        {
            errors["body"] = ["A JSON body with encryptedPan and corporateId is required."];
        }
        else
        {
            if (string.IsNullOrWhiteSpace(body.EncryptedPan))
                errors["encryptedPan"] = ["Required: the PAN as a compact JWE."];
            if (string.IsNullOrWhiteSpace(body.CorporateId))
                errors["corporateId"] = ["Required."];
            else if (body.CorporateId.Length > MaxCorporateIdLength)
                errors["corporateId"] = [$"At most {MaxCorporateIdLength} characters."];
        }

        if (errors.Count > 0)
            return TypedResults.ValidationProblem(errors, title: "The request is invalid.");

        // The fallback and endpoint policies guarantee an authenticated caller; azp is the OAuth client ID.
        var clientId = http.User.FindFirstValue("azp") ?? http.User.FindFirstValue("appid");
        if (string.IsNullOrEmpty(clientId))
            return Problem(StatusCodes.Status403Forbidden, ErrorCodes.Forbidden, "The access token does not identify the calling client.");

        var command = new CardChangeCommand(action, body!.EncryptedPan!, body.CorporateId!, initiatingUser!, clientId);
        var result = await service.ChangeAsync(command, ct);

        return result.Error is { } error
            ? ToProblem(error)
            : TypedResults.Ok(ToResponse(action, result.Card!));
    }

    /// <summary>Exactly one value, trimmed, 1-128 characters, no control characters. Otherwise opaque.</summary>
    private static string? ReadInitiatingUser(HttpRequest request, Dictionary<string, string[]> errors)
    {
        var values = request.Headers[InitiatingUserHeader];
        if (values.Count > 1)
        {
            errors[InitiatingUserHeader] = ["Send exactly one initiating user."];
            return null;
        }

        var value = values.ToString().Trim();
        if (value.Length == 0)
            errors[InitiatingUserHeader] = ["Required: the ID of the person who started this action."];
        else if (value.Length > MaxInitiatingUserLength)
            errors[InitiatingUserHeader] = [$"At most {MaxInitiatingUserLength} characters."];
        else if (value.Any(char.IsControl))
            errors[InitiatingUserHeader] = ["Must not contain control characters."];
        else
            return value;

        return null;
    }

    private static CardChangeResponse ToResponse(CardAction action, CardDetails card) =>
        new(action.ToString(), card.CorporateId!, card.MaskedPan, card.Status.ToString(), card.UpdatedAt);

    private static ProblemHttpResult ToProblem(CardControlError error) => error switch
    {
        CardControlError.CorporateNotEnabled => Problem(StatusCodes.Status403Forbidden, ErrorCodes.CorporateNotEnabled,
            "The partner is not enabled for this corporate."),
        CardControlError.InvalidEncryptedPan => Problem(StatusCodes.Status400BadRequest, ErrorCodes.InvalidEncryptedPan,
            "encryptedPan could not be decrypted. It must be a compact JWE (RSA-OAEP-256, A256GCM) for a current bank key."),
        CardControlError.InvalidPan => Problem(StatusCodes.Status400BadRequest, ErrorCodes.InvalidPan,
            "The decrypted PAN is not a valid card number."),
        CardControlError.CardNotFound => Problem(StatusCodes.Status404NotFound, ErrorCodes.CardNotFound,
            "No card matches this PAN and corporate."),
        CardControlError.CardAlreadyLocked => Problem(StatusCodes.Status409Conflict, ErrorCodes.CardAlreadyLocked,
            "The card is already locked."),
        CardControlError.CardNotLocked => Problem(StatusCodes.Status409Conflict, ErrorCodes.CardNotLocked,
            "The card is not locked."),
        CardControlError.CardCancelled => Problem(StatusCodes.Status422UnprocessableEntity, ErrorCodes.CardCancelled,
            "The card is cancelled; it can't be locked or unlocked."),
        _ => throw new InvalidOperationException($"Unhandled error {error}."),
    };

    private static ProblemHttpResult Problem(int status, string code, string title) =>
        ProblemDetailsSetup.Problem(status, code, title);
}
