using System.Security.Cryptography;
using CardControl.Api.Security;

namespace CardControl.Api.Tests.Security;

public sealed class PanKeyRingTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pan-keys-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Loads_a_pkcs8_pem_file_relative_to_the_content_root()
    {
        using var rsa = RSA.Create(3072);
        File.WriteAllText(Path.Combine(_dir, "bank-dev-1.private.pem"), rsa.ExportPkcs8PrivateKeyPem());

        using var ring = new PanKeyRing(Options(new PanKeyOptions { Kid = "bank-dev-1", PrivateKeyPath = "bank-dev-1.private.pem" }), _dir);

        Assert.True(ring.TryGetKey("bank-dev-1", out var key));
        Assert.Equal(3072, key!.KeySize);
        Assert.False(ring.TryGetKey("BANK-DEV-1", out _));
    }

    [Fact]
    public void Loads_an_inline_pem()
    {
        using var rsa = RSA.Create(2048);

        using var ring = new PanKeyRing(Options(new PanKeyOptions { Kid = "k1", PrivateKeyPem = rsa.ExportPkcs8PrivateKeyPem() }), _dir);

        Assert.Equal(["k1"], ring.KeyIds);
    }

    [Fact]
    public void Fails_when_no_keys_are_configured()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new PanKeyRing(new PanEncryptionOptions(), _dir));
        Assert.Contains("No PAN decryption keys", ex.Message);
    }

    [Fact]
    public void Fails_with_a_helpful_message_when_the_key_file_is_missing()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new PanKeyRing(Options(new PanKeyOptions { Kid = "k1", PrivateKeyPath = "missing.pem" }), _dir));
        Assert.Contains("jwe-tool.cs -- keygen", ex.Message);
    }

    [Fact]
    public void Fails_for_a_public_key_because_it_cannot_decrypt()
    {
        using var rsa = RSA.Create(3072);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new PanKeyRing(Options(new PanKeyOptions { Kid = "k1", PrivateKeyPem = rsa.ExportSubjectPublicKeyInfoPem() }), _dir));
        Assert.Contains("not a usable RSA private key", ex.Message);
    }

    [Fact]
    public void Fails_for_a_key_below_2048_bits()
    {
        using var rsa = RSA.Create(1024);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new PanKeyRing(Options(new PanKeyOptions { Kid = "k1", PrivateKeyPem = rsa.ExportPkcs8PrivateKeyPem() }), _dir));
        Assert.Contains("1024 bits", ex.Message);
    }

    [Fact]
    public void Fails_for_garbage_without_echoing_it()
    {
        const string garbage = "-----BEGIN PRIVATE KEY-----\nSECRETISH\n-----END PRIVATE KEY-----";

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new PanKeyRing(Options(new PanKeyOptions { Kid = "k1", PrivateKeyPem = garbage }), _dir));
        Assert.DoesNotContain("SECRETISH", ex.ToString());
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Fails_unless_exactly_one_key_source_is_set(bool path, bool pem)
    {
        var options = new PanKeyOptions
        {
            Kid = "k1",
            PrivateKeyPath = path ? "a.pem" : null,
            PrivateKeyPem = pem ? "pem" : null,
        };

        var ex = Assert.Throws<InvalidOperationException>(() => new PanKeyRing(Options(options), _dir));
        Assert.Contains("exactly one", ex.Message);
    }

    [Fact]
    public void Fails_for_a_missing_or_duplicate_kid()
    {
        using var rsa = RSA.Create(2048);
        var pem = rsa.ExportPkcs8PrivateKeyPem();

        Assert.Throws<InvalidOperationException>(() => new PanKeyRing(Options(new PanKeyOptions { Kid = "", PrivateKeyPem = pem }), _dir));
        Assert.Throws<InvalidOperationException>(() => new PanKeyRing(
            Options(new PanKeyOptions { Kid = "k1", PrivateKeyPem = pem }, new PanKeyOptions { Kid = "k1", PrivateKeyPem = pem }), _dir));
    }

    private static PanEncryptionOptions Options(params PanKeyOptions[] keys) => new() { Keys = [.. keys] };
}
