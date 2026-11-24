using System.Net;
using System.Text.Json;
using CardControl.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace CardControl.Api.Tests;

public class ErrorHandlingTests(CardControlApiFactory factory) : IClassFixture<CardControlApiFactory>
{
    private const string SecretInExceptionMessage = "4111111111111111";

    [Fact]
    public async Task Health_endpoint_responds()
    {
        var response = await factory.CreateClient().GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_route_returns_problem_details_with_code_and_trace_id()
    {
        var response = await factory.CreateClient().GetAsync("/no-such-route");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await ReadJson(response);
        Assert.Equal(404, problem.GetProperty("status").GetInt32());
        Assert.Equal("NotFound", problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task Server_header_is_not_sent()
    {
        var response = await factory.CreateClient().GetAsync("/healthz");

        Assert.Empty(response.Headers.Server);
    }

    [Fact]
    public async Task Unhandled_exception_returns_generic_500_and_keeps_exception_message_out_of_response_and_logs()
    {
        // Adds a terminal middleware after the app's endpoints, so /boom runs inside the app's
        // exception handler and throws an exception whose message holds sensitive data.
        await using var throwing = new ThrowingFactory();

        var response = await throwing.CreateClient().GetAsync("/boom");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal("InternalError", problem.GetProperty("code").GetString());
        Assert.DoesNotContain(SecretInExceptionMessage, body);
        Assert.DoesNotContain("InvalidOperationException", body);

        Assert.Contains(throwing.Logs.Entries, e => e.Contains("Unhandled System.InvalidOperationException"));
        Assert.DoesNotContain(SecretInExceptionMessage, throwing.Logs.AllText);
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private sealed class ThrowingFactory : CardControlApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services => services.AddTransient<IStartupFilter, ThrowAfterEndpoints>());
        }
    }

    private sealed class ThrowAfterEndpoints : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            app.Run(ctx => ctx.Request.Path == "/boom"
                ? throw new InvalidOperationException($"PAN {SecretInExceptionMessage} must never leak")
                : Task.CompletedTask);
        };
    }
}
