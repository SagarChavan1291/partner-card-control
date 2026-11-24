using System.Net;
using System.Text.Json;
using CardControl.Api.Downstream;
using CardControl.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace CardControl.Api.Tests;

/// <summary>A DownstreamException thrown anywhere in a request becomes a 502/503/504 Problem Details.</summary>
public sealed class DownstreamErrorMappingTests : IClassFixture<DownstreamErrorMappingTests.Factory>
{
    private readonly Factory _factory;

    public DownstreamErrorMappingTests(Factory factory) => _factory = factory;

    [Theory]
    [InlineData(DownstreamFailure.Unavailable, HttpStatusCode.ServiceUnavailable, "ServiceUnavailable")]
    [InlineData(DownstreamFailure.Timeout, HttpStatusCode.GatewayTimeout, "GatewayTimeout")]
    [InlineData(DownstreamFailure.BadResponse, HttpStatusCode.BadGateway, "BadGateway")]
    public async Task Maps_failure_to_gateway_status(DownstreamFailure failure, HttpStatusCode status, string code)
    {
        var response = await _factory.CreateClient(_factory.Tokens.CreateWithWriteRole()).GetAsync($"/throw/{failure}");

        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.True(problem.TryGetProperty("traceId", out _));

        // Which internal service failed, and what it said, stay internal.
        Assert.DoesNotContain("CardPlatform", body);
        Assert.DoesNotContain("CardCancelledSecretDetail", body);
    }

    [Fact]
    public async Task Unavailable_tells_the_partner_when_to_retry()
    {
        var response = await _factory.CreateClient(_factory.Tokens.CreateWithWriteRole()).GetAsync("/throw/Unavailable");

        Assert.Equal("5", response.Headers.GetValues("Retry-After").Single());
    }

    [Fact]
    public async Task Failures_are_logged_with_service_and_status()
    {
        await _factory.CreateClient(_factory.Tokens.CreateWithWriteRole()).GetAsync("/throw/BadResponse");

        Assert.Contains(_factory.Logs.Entries, e =>
            e.Contains("Downstream failure: CardPlatform GetCard BadResponse") && e.Contains("status 418"));
    }

    public sealed class Factory : CardControlApiFactory
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
            app.Run(ctx =>
            {
                var failure = Enum.Parse<DownstreamFailure>(ctx.Request.Path.Value!["/throw/".Length..]);
                throw new DownstreamException(DownstreamServices.CardPlatform, "GetCard", failure, 418, "CardCancelledSecretDetail");
            });
        };
    }
}
