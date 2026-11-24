using CardControl.Api.Errors;
using CardControl.Api.Security;

namespace CardControl.Api.Endpoints;

public static class CardEndpoints
{
    public static IEndpointRouteBuilder MapCardEndpoints(this IEndpointRouteBuilder app)
    {
        var cards = app.MapGroup("/v1/cards")
            .RequireAuthorization(Policies.CardControlWrite)
            .WithTags("Cards");

        cards.MapPost("/lock", NotYetImplemented).WithName("LockCard");
        cards.MapPost("/unlock", NotYetImplemented).WithName("UnlockCard");

        return app;
    }

    // Placeholder until the lock/unlock flow lands: lets the auth pipeline be tested end to end now.
    private static IResult NotYetImplemented() =>
        ProblemDetailsSetup.Problem(StatusCodes.Status501NotImplemented, "NotImplemented", "Not implemented yet");
}
