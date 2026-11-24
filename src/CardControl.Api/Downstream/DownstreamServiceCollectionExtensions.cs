using Microsoft.Extensions.Options;

namespace CardControl.Api.Downstream;

public static class DownstreamServiceCollectionExtensions
{
    public static IServiceCollection AddDownstreamClients(this IServiceCollection services, IConfiguration configuration)
    {
        AddOptions(services, configuration, DownstreamServices.Tokenisation);
        AddOptions(services, configuration, DownstreamServices.CardPlatform);

        services.AddHttpClient<ITokenisationClient, TokenisationClient>((sp, http) =>
            Configure(http, sp, DownstreamServices.Tokenisation));
        services.AddHttpClient<ICardPlatformClient, CardPlatformClient>((sp, http) =>
            Configure(http, sp, DownstreamServices.CardPlatform));

        return services;
    }

    private static void AddOptions(IServiceCollection services, IConfiguration configuration, string name) =>
        services.AddOptions<DownstreamServiceOptions>(name)
            .Bind(configuration.GetSection($"{DownstreamServices.SectionName}:{name}"))
            .ValidateDataAnnotations()
            .ValidateOnStart();

    private static void Configure(HttpClient http, IServiceProvider sp, string name)
    {
        var options = sp.GetRequiredService<IOptionsMonitor<DownstreamServiceOptions>>().Get(name);
        // Trailing slash so relative paths ("tokens", "cards/...") append rather than replace the last segment.
        http.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
        http.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }
}
