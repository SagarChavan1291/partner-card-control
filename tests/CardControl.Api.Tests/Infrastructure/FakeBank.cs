using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CardControl.Api.Tests.Infrastructure;

/// <summary>
/// In-memory stand-in for both starter-kit stubs (tokenisation at http://tokenisation.test, card platform at
/// http://cards.test), following their documented contracts: same seed data, lock rules, RFC 9457 errors
/// with a <c>code</c>, and an audit trail that records every lock/unlock attempt, rejected ones included.
/// <see cref="Fault"/> lets a test override any response (503s, delays...).
/// </summary>
public sealed class FakeBank : HttpMessageHandler
{
    public const string TokenisationBaseUrl = "http://tokenisation.test";
    public const string CardPlatformBaseUrl = "http://cards.test";

    public static readonly IReadOnlyList<SeedCard> Seed =
    [
        new("4111111111111111", "ctok_8f3a1c2e9b7d4a01", "CORP-1001", "Aroha Ngata", "411111******1111", "Active"),
        new("4242424242424242", "ctok_2b9e7f4c1a6d3e02", "CORP-1001", "Wiremu Tane", "424242******4242", "Locked"),
        new("4012888888881881", "ctok_5d1c8a3f7e2b9c03", "CORP-2002", "Mere Parata", "401288******1881", "Active"),
        new("4000056655665556", "ctok_9e4b2d7a1c8f5e04", "CORP-2002", "Hemi Walker", "400005******5556", "Cancelled"),
        new("4000002500003155", "ctok_3a7f9c1e5b2d8a05", "CORP-3003", "Kiri Moana", "400000******3155", "Active"),
        new("4000000000003220", "ctok_7c2e5a9f3d1b6c06", null, "Tama Rangi", "400000******3220", "Active"),
    ];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly Lock _gate = new();
    private Dictionary<string, string> _tokenByPan = [];
    private Dictionary<string, CardState> _cards = [];

    public FakeBank() => Reset();

    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

    public ConcurrentQueue<AuditEntry> Audit { get; } = new();

    /// <summary>Return a response to short-circuit a request, or null to let the fake handle it.</summary>
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage?>>? Fault { get; set; }

    public void Reset()
    {
        lock (_gate)
        {
            _tokenByPan = Seed.ToDictionary(c => c.Pan, c => c.Token);
            _cards = Seed.ToDictionary(c => c.Token, c => new CardState(c));
        }
        Requests.Clear();
        Audit.Clear();
        Fault = null;
    }

    public string StatusOf(string token)
    {
        lock (_gate)
            return _cards[token].Status;
    }

    public IEnumerable<RecordedRequest> RequestsTo(string pathPrefix) =>
        Requests.Where(r => r.Uri.AbsolutePath.StartsWith(pathPrefix, StringComparison.Ordinal));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Enqueue(new RecordedRequest(request.Method, request.RequestUri!, body));

        if (Fault is not null && await Fault(request, cancellationToken) is { } faulted)
            return faulted;

        var uri = request.RequestUri!;
        var segments = uri.AbsolutePath.Trim('/').Split('/');

        return (uri.Host, request.Method.Method, segments) switch
        {
            ("tokenisation.test", "POST", ["tokens"]) => Tokenise(body),
            ("cards.test", "GET", ["cards", var token]) => GetCard(token),
            ("cards.test", "POST", ["cards", var token, "lock"]) => Change(token, "Lock", body),
            ("cards.test", "POST", ["cards", var token, "unlock"]) => Change(token, "Unlock", body),
            _ => Problem(HttpStatusCode.NotFound, "NotFound"),
        };
    }

    private HttpResponseMessage Tokenise(string? body)
    {
        var pan = JsonDocument.Parse(body ?? "{}").RootElement.TryGetProperty("pan", out var p) ? p.GetString() : null;
        if (pan is null || pan.Length is < 12 or > 19 || !pan.All(char.IsAsciiDigit) || !Luhn(pan))
            return Problem(HttpStatusCode.BadRequest, "InvalidPan");

        lock (_gate)
        {
            if (!_tokenByPan.TryGetValue(pan, out var token))
                _tokenByPan[pan] = token = "ctok_" + Guid.NewGuid().ToString("N")[..16];
            return Ok(new { cardToken = token });
        }
    }

    private HttpResponseMessage GetCard(string token)
    {
        lock (_gate)
            return _cards.TryGetValue(token, out var card) ? Ok(card.ToJson()) : Problem(HttpStatusCode.NotFound, "CardNotFound");
    }

    private HttpResponseMessage Change(string token, string action, string? body)
    {
        var request = JsonDocument.Parse(body ?? "{}").RootElement;
        var user = request.TryGetProperty("initiatingUser", out var u) ? u.GetString()?.Trim() : null;
        var channel = request.TryGetProperty("channel", out var c) ? c.GetString() : null;
        if (string.IsNullOrEmpty(user) || user.Length > 128)
            return Problem(HttpStatusCode.BadRequest, "InvalidRequest");

        lock (_gate)
        {
            if (!_cards.TryGetValue(token, out var card))
                return Problem(HttpStatusCode.NotFound, "CardNotFound");

            var (status, code) = (card.Status, action) switch
            {
                ("Cancelled", _) => (HttpStatusCode.UnprocessableEntity, "CardCancelled"),
                ("Locked", "Lock") => (HttpStatusCode.Conflict, "CardAlreadyLocked"),
                ("Active", "Unlock") => (HttpStatusCode.Conflict, "CardNotLocked"),
                _ => (HttpStatusCode.OK, "Success"),
            };

            Audit.Enqueue(new AuditEntry(token, action, code, user, channel));

            if (status != HttpStatusCode.OK)
                return Problem(status, code);

            card.Status = action == "Lock" ? "Locked" : "Active";
            card.UpdatedAt = DateTimeOffset.UtcNow;
            return Ok(card.ToJson());
        }
    }

    public static HttpResponseMessage Problem(HttpStatusCode status, string code) =>
        new(status)
        {
            Content = new StringContent(
                $$"""{"type":"about:blank","title":"{{code}}","status":{{(int)status}},"code":"{{code}}"}""",
                Encoding.UTF8, "application/problem+json"),
        };

    private static HttpResponseMessage Ok(object body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json") };

    private static bool Luhn(string pan)
    {
        var sum = 0;
        for (var i = 0; i < pan.Length; i++)
        {
            var d = pan[pan.Length - 1 - i] - '0';
            if (i % 2 == 1 && (d *= 2) > 9) d -= 9;
            sum += d;
        }
        return sum % 10 == 0;
    }

    public sealed record SeedCard(string Pan, string Token, string? CorporateId, string Name, string MaskedPan, string Status);

    public sealed record AuditEntry(string CardToken, string Action, string Outcome, string InitiatingUser, string? Channel);

    private sealed class CardState(SeedCard seed)
    {
        public string Status { get; set; } = seed.Status;
        public DateTimeOffset UpdatedAt { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public object ToJson() => new
        {
            cardToken = seed.Token,
            corporateId = seed.CorporateId,
            cardholderName = seed.Name,
            maskedPan = seed.MaskedPan,
            status = Status,
            updatedAt = UpdatedAt,
        };
    }
}
