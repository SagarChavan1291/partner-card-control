using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace CardControl.Api.Tests.Infrastructure;

/// <summary>Builds lock/unlock requests the way the partner sends them.</summary>
public static class ApiRequests
{
    public const string DefaultUser = "jane.smith@acme.example";

    public static HttpRequestMessage Change(string route, string? encryptedPan, string? corporateId,
        string? initiatingUser = DefaultUser)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = JsonContent.Create(new { encryptedPan, corporateId }),
        };
        if (initiatingUser is not null)
            request.Headers.TryAddWithoutValidation("X-Initiating-User", initiatingUser);
        return request;
    }

    public static HttpRequestMessage RawJson(string route, string json, string? initiatingUser = DefaultUser)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (initiatingUser is not null)
            request.Headers.TryAddWithoutValidation("X-Initiating-User", initiatingUser);
        return request;
    }

    public static async Task<JsonElement> Json(this HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
}
