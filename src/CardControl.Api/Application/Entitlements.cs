using Microsoft.Extensions.Options;

namespace CardControl.Api.Application;

/// <summary>
/// Which corporates each partner client may act for. Held by this API, not the card platform.
/// Keyed by OAuth client ID (the token's <c>azp</c>), so adding a second partner is configuration, not code.
/// </summary>
public sealed class EntitlementOptions
{
    public const string SectionName = "Entitlements";

    public Dictionary<string, string[]> Clients { get; set; } = [];
}

public interface IEntitlementPolicy
{
    bool IsCorporateEnabled(string clientId, string corporateId);
}

/// <summary>
/// Config-backed. Exact, case-sensitive matching for both client and corporate IDs.
/// In production this would be a store the bank's operations team manages, with change history.
/// </summary>
public sealed class ConfiguredEntitlementPolicy : IEntitlementPolicy
{
    private readonly Dictionary<string, HashSet<string>> _enabled;

    public ConfiguredEntitlementPolicy(IOptions<EntitlementOptions> options)
    {
        _enabled = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (clientId, corporates) in options.Value.Clients)
            _enabled[clientId] = new HashSet<string>(corporates.Where(c => !string.IsNullOrWhiteSpace(c)), StringComparer.Ordinal);
    }

    public bool IsCorporateEnabled(string clientId, string corporateId) =>
        _enabled.TryGetValue(clientId, out var corporates) && corporates.Contains(corporateId);
}
