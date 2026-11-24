using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace CardControl.Api.Security;

/// <summary>The bank's private keys for decrypting partner-encrypted PANs, by key ID (JWE <c>kid</c>).</summary>
public sealed class PanEncryptionOptions
{
    public const string SectionName = "PanEncryption";

    /// <summary>
    /// More than one key during rotation: publish the new public key to the partner, keep the old private
    /// key here until the partner has switched, then remove it.
    /// </summary>
    public List<PanKeyOptions> Keys { get; set; } = [];
}

public sealed class PanKeyOptions
{
    public string Kid { get; set; } = "";

    /// <summary>Path to a PKCS#8 PEM private key, relative to the content root. Local development.</summary>
    public string? PrivateKeyPath { get; set; }

    /// <summary>The PEM itself, e.g. from a mounted Kubernetes secret / Key Vault CSI driver.</summary>
    public string? PrivateKeyPem { get; set; }
}

/// <summary>
/// Loads and checks every key at start-up, so a missing, unreadable or weak key stops the app
/// instead of failing the first partner request.
/// </summary>
public sealed class PanKeyRing : IDisposable
{
    public const int MinimumKeySizeBits = 2048;

    private readonly Dictionary<string, RSA> _keys;

    public PanKeyRing(IOptions<PanEncryptionOptions> options, IHostEnvironment environment)
        : this(options.Value, environment.ContentRootPath) { }

    public PanKeyRing(PanEncryptionOptions options, string contentRoot)
    {
        if (options.Keys.Count == 0)
            throw new InvalidOperationException($"No PAN decryption keys configured under '{PanEncryptionOptions.SectionName}:Keys'.");

        _keys = new Dictionary<string, RSA>(StringComparer.Ordinal);
        try
        {
            foreach (var key in options.Keys)
                _keys.Add(ValidKid(key.Kid), Load(key, contentRoot));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public IReadOnlyCollection<string> KeyIds => _keys.Keys;

    // RSA instances are safe for concurrent decrypt operations; the keys are never mutated after start-up.
    public bool TryGetKey(string kid, out RSA? key) => _keys.TryGetValue(kid, out key);

    public void Dispose()
    {
        foreach (var key in _keys.Values)
            key.Dispose();
        _keys.Clear();
    }

    private string ValidKid(string kid)
    {
        if (string.IsNullOrWhiteSpace(kid))
            throw new InvalidOperationException("Every PAN decryption key needs a 'Kid'.");
        if (_keys.ContainsKey(kid))
            throw new InvalidOperationException($"Duplicate PAN decryption key id '{kid}'.");
        return kid;
    }

    private static RSA Load(PanKeyOptions key, string contentRoot)
    {
        var hasPath = !string.IsNullOrWhiteSpace(key.PrivateKeyPath);
        var hasPem = !string.IsNullOrWhiteSpace(key.PrivateKeyPem);
        if (hasPath == hasPem)
            throw new InvalidOperationException($"PAN key '{key.Kid}': set exactly one of PrivateKeyPath or PrivateKeyPem.");

        string pem;
        if (hasPath)
        {
            var path = Path.GetFullPath(key.PrivateKeyPath!, contentRoot);
            if (!File.Exists(path))
                throw new InvalidOperationException(
                    $"PAN key '{key.Kid}': private key file '{path}' not found. Run 'dotnet run tools/jwe-tool.cs -- keygen' from the repository root.");
            pem = File.ReadAllText(path);
        }
        else
        {
            pem = key.PrivateKeyPem!;
        }

        var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(pem);
            // A public-only key imports fine but can't decrypt: catch that now, not on the first request.
            rsa.ExportParameters(includePrivateParameters: true);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            rsa.Dispose();
            // Don't chain the inner exception: its message could quote parts of the key material.
            throw new InvalidOperationException($"PAN key '{key.Kid}' is not a usable RSA private key in PEM format.");
        }

        if (rsa.KeySize < MinimumKeySizeBits)
        {
            rsa.Dispose();
            throw new InvalidOperationException($"PAN key '{key.Kid}' is {rsa.KeySize} bits; at least {MinimumKeySizeBits} required.");
        }

        return rsa;
    }
}
