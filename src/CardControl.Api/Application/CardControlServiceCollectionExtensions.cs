namespace CardControl.Api.Application;

public static class CardControlServiceCollectionExtensions
{
    public static IServiceCollection AddCardControl(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<EntitlementOptions>(configuration.GetSection(EntitlementOptions.SectionName));
        services.AddSingleton<IEntitlementPolicy, ConfiguredEntitlementPolicy>();
        services.AddScoped<CardControlService>();
        return services;
    }
}
