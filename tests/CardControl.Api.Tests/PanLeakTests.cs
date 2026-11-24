using System.Net;
using System.Text;
using CardControl.Api.Tests.Infrastructure;

namespace CardControl.Api.Tests;

/// <summary>
/// Drives every path through the API (successes, every error, downstream failures, a downstream that echoes the
/// PAN back, an exception carrying the PAN) with all logging on at Trace, then checks that no clear PAN appears
/// in any log line, response body or response header. Also checks the encrypted PAN isn't logged either.
/// </summary>
public sealed class PanLeakTests : IClassFixture<CardControlApiFactory>
{
    private readonly CardControlApiFactory _api;

    public PanLeakTests(CardControlApiFactory api) => _api = api;

    [Fact]
    public async Task No_pan_appears_in_logs_responses_or_headers_on_any_path()
    {
        var bank = _api.Bank;
        bank.Reset();
        var client = _api.CreateClient(_api.Tokens.CreateWithWriteRole());
        var responses = new List<string>();
        var jwes = new List<string>();

        async Task Send(HttpRequestMessage request)
        {
            using var response = await client.SendAsync(request);
            var text = new StringBuilder(await response.Content.ReadAsStringAsync());
            foreach (var header in response.Headers.Concat(response.Content.Headers))
                text.Append($"\n{header.Key}: {string.Join(",", header.Value)}");
            responses.Add($"{(int)response.StatusCode} {text}");
        }

        Task Change(string route, string pan, string corporateId)
        {
            var jwe = _api.EncryptPan(pan);
            jwes.Add(jwe);
            return Send(ApiRequests.Change(route, jwe, corporateId));
        }

        // Successes and every business outcome, for every seed card.
        foreach (var card in FakeBank.Seed)
        {
            foreach (var corporate in new[] { "CORP-1001", "CORP-2002", "CORP-3003" })
            {
                await Change("/v1/cards/lock", card.Pan, corporate);
                await Change("/v1/cards/unlock", card.Pan, corporate);
            }
        }

        // Not a card the bank knows; and a value that decrypts but isn't a valid PAN.
        await Change("/v1/cards/lock", "5555555555554444", "CORP-1001");
        await Change("/v1/cards/lock", "4111111111111112", "CORP-1001");

        // Partner mistakes: the clear PAN where the JWE should be, as a JSON number, in an extra field.
        await Send(ApiRequests.Change("/v1/cards/lock", "4111111111111111", "CORP-1001"));
        await Send(ApiRequests.RawJson("/v1/cards/lock", """{"encryptedPan":4111111111111111,"corporateId":"CORP-1001"}"""));
        await Send(ApiRequests.RawJson("/v1/cards/lock", """{"pan":"4111111111111111","corporateId":"CORP-1001"}"""));
        await Send(ApiRequests.RawJson("/v1/cards/lock", "{\"encryptedPan\":\"4111111111111111\""));   // truncated JSON

        // Downstream failures, including a misbehaving tokeniser that echoes the PAN in its error detail.
        bank.Fault = (req, _) => Task.FromResult<HttpResponseMessage?>(req.RequestUri!.Host == "tokenisation.test"
            ? new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(
                    """{"title":"Bad","status":400,"code":"Weird","detail":"PAN 4111111111111111 rejected"}""",
                    Encoding.UTF8, "application/problem+json"),
            }
            : null);
        await Change("/v1/cards/lock", "4111111111111111", "CORP-1001");

        bank.Fault = (req, _) => Task.FromResult(req.RequestUri!.Host == "cards.test"
            ? FakeBank.Problem(HttpStatusCode.ServiceUnavailable, "ServiceUnavailable")
            : null);
        await Change("/v1/cards/lock", "4012888888881881", "CORP-2002");

        // An unexpected exception whose message carries the PAN: the generic 500 path.
        bank.Fault = (req, _) => req.RequestUri!.Host == "tokenisation.test"
            ? throw new InvalidOperationException("boom while handling 4111111111111111")
            : Task.FromResult<HttpResponseMessage?>(null);
        await Change("/v1/cards/lock", "4111111111111111", "CORP-1001");
        bank.Fault = null;

        // Sanity: the scenarios really ran, and logging really was captured at all levels.
        Assert.Contains(responses, r => r.StartsWith("200 "));
        Assert.Contains(responses, r => r.StartsWith("500 "));
        Assert.Contains(_api.Logs.Entries, e => e.Contains("ctok_"));
        Assert.Contains(_api.Logs.Entries, e => e.StartsWith("Trace") || e.StartsWith("Debug"));

        var logs = _api.Logs.AllText;
        var allResponses = string.Join("\n", responses);
        string[] pans = [.. FakeBank.Seed.Select(c => c.Pan), "5555555555554444", "4111111111111112"];
        foreach (var pan in pans)
        {
            Assert.False(logs.Contains(pan), $"PAN {pan[^4..]} found in logs:\n" +
                string.Join("\n", _api.Logs.Entries.Where(e => e.Contains(pan)).Select(e => e.Replace(pan, "<PAN>")).Distinct()));
            Assert.False(allResponses.Contains(pan), $"PAN {pan[^4..]} found in a response");
            // Partial PANs too: more than the first six digits is more than PCI DSS allows to be displayed.
            Assert.False(logs.Contains(pan[..12]), $"first 12 digits of PAN {pan[^4..]} found in logs");
        }
        foreach (var jwe in jwes)
            Assert.False(logs.Contains(jwe.Split('.')[1]), "encrypted PAN found in logs");
    }
}
