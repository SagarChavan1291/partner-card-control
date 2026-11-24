using System.Net;
using System.Text;

namespace CardControl.Api.Tests.Infrastructure;

/// <summary>
/// A scripted downstream service: answers each request with the given function and records what was sent.
/// The cancellation token is passed through so HttpClient's timeout works as it does with a real handler.
/// </summary>
public sealed class StubHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : this((request, _) => Task.FromResult(respond(request))) { }

    public List<RecordedRequest> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, body));
        return await respond(request, cancellationToken);
    }

    public HttpClient CreateClient(string baseAddress) =>
        new(this) { BaseAddress = new Uri(baseAddress), Timeout = TimeSpan.FromSeconds(5) };

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>An RFC 9457 error shaped like the stubs' (Problem Details with a <c>code</c>).</summary>
    public static HttpResponseMessage Problem(HttpStatusCode status, string code) =>
        new(status)
        {
            Content = new StringContent(
                $$"""{"type":"about:blank","title":"{{code}}","status":{{(int)status}},"detail":"detail text","code":"{{code}}"}""",
                Encoding.UTF8, "application/problem+json"),
        };
}

public sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Body);
