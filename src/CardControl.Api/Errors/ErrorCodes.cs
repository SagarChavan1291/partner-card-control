namespace CardControl.Api.Errors;

/// <summary>
/// Machine-readable error codes returned in the <c>code</c> field of every Problem Details response.
/// Partners should branch on these, not on <c>title</c> or <c>detail</c>.
/// </summary>
public static class ErrorCodes
{
    public const string InvalidRequest = "InvalidRequest";
    public const string Unauthorized = "Unauthorized";
    public const string Forbidden = "Forbidden";
    public const string NotFound = "NotFound";
    public const string MethodNotAllowed = "MethodNotAllowed";
    public const string UnsupportedMediaType = "UnsupportedMediaType";
    public const string PayloadTooLarge = "PayloadTooLarge";
    public const string InternalError = "InternalError";
    public const string BadGateway = "BadGateway";
    public const string ServiceUnavailable = "ServiceUnavailable";
    public const string GatewayTimeout = "GatewayTimeout";

    /// <summary>Default code for a status when the code path that produced the error didn't set one.</summary>
    public static string ForStatus(int status) => status switch
    {
        StatusCodes.Status400BadRequest => InvalidRequest,
        StatusCodes.Status401Unauthorized => Unauthorized,
        StatusCodes.Status403Forbidden => Forbidden,
        StatusCodes.Status404NotFound => NotFound,
        StatusCodes.Status405MethodNotAllowed => MethodNotAllowed,
        StatusCodes.Status413PayloadTooLarge => PayloadTooLarge,
        StatusCodes.Status415UnsupportedMediaType => UnsupportedMediaType,
        _ => InternalError,
    };
}
