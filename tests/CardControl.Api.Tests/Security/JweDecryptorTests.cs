using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using CardControl.Api.Security;
using CardControl.Api.Tests.Infrastructure;

namespace CardControl.Api.Tests.Security;

/// <summary>One bank key for the whole class: RSA-3072 generation is slow, and xUnit news up the class per test.</summary>
public sealed class BankKeyFixture : IDisposable
{
    public RSA Key { get; } = RSA.Create(3072);

    public void Dispose() => Key.Dispose();
}

public sealed class JweDecryptorTests : IClassFixture<BankKeyFixture>, IDisposable
{
    private const string Pan = "4111111111111111";

    private readonly RSA _bankKey;
    private readonly PanKeyRing _keyRing;
    private readonly JweDecryptor _decryptor;

    public JweDecryptorTests(BankKeyFixture fixture)
    {
        _bankKey = fixture.Key;
        _keyRing = KeyRingWith((TestJwe.DefaultKid, _bankKey));
        _decryptor = new JweDecryptor(_keyRing);
    }

    public void Dispose() => _keyRing.Dispose();

    // ---- Happy path ----

    [Theory]
    [InlineData("4111111111111111")]
    [InlineData("4242424242424242")]
    [InlineData("4012888888881881")]
    [InlineData("4000056655665556")]
    [InlineData("4000002500003155")]
    [InlineData("4000000000003220")]
    [InlineData("378282246310005")]       // 15-digit Amex test number
    [InlineData("6011000990139424")]
    public void Decrypts_a_pan_encrypted_the_way_the_partner_does(string pan)
    {
        var result = _decryptor.Decrypt(TestJwe.Encrypt(pan, _bankKey));

        Assert.Equal(pan, result.RevealDigits());
    }

    [Fact]
    public void Picks_the_key_named_by_kid_so_keys_can_be_rotated()
    {
        using var oldKey = RSA.Create(2048);
        using var newKey = RSA.Create(2048);
        using var ring = KeyRingWith(("bank-2025", oldKey), ("bank-2026", newKey));
        var decryptor = new JweDecryptor(ring);

        Assert.Equal(Pan, decryptor.Decrypt(TestJwe.Encrypt(Pan, oldKey, "bank-2025")).RevealDigits());
        Assert.Equal(Pan, decryptor.Decrypt(TestJwe.Encrypt(Pan, newKey, "bank-2026")).RevealDigits());
    }

    [Fact]
    public void Header_members_may_come_in_any_order_and_carry_extra_registered_fields()
    {
        var header = $$"""{"kid":"{{TestJwe.DefaultKid}}","typ":"JOSE","enc":"A256GCM","alg":"RSA-OAEP-256"}""";

        var result = _decryptor.Decrypt(TestJwe.Encrypt(Encoding.UTF8.GetBytes(Pan), _bankKey, header));

        Assert.Equal(Pan, result.RevealDigits());
    }

    // ---- Malformed input ----

    [Theory]
    [InlineData("")]
    [InlineData("not-a-jwe")]
    [InlineData("a.b.c")]                 // a JWS/JWT shape, not a JWE
    [InlineData("a.b.c.d")]
    [InlineData("a.b.c.d.e.f")]
    [InlineData("!!!.b.c.d.e")]           // not base64url
    [InlineData("....")]
    public void Rejects_input_that_is_not_a_compact_jwe(string input)
    {
        AssertFails(input, PanDecryptionFailure.Malformed);
    }

    [Fact]
    public void Rejects_oversized_input_before_doing_any_crypto()
    {
        AssertFails(new string('A', 5000), PanDecryptionFailure.Malformed);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("\"RSA-OAEP-256\"")]
    public void Rejects_a_header_that_is_not_a_json_object(string header)
    {
        AssertFails(TestJwe.Encrypt(Encoding.UTF8.GetBytes(Pan), _bankKey, header),
            PanDecryptionFailure.Malformed, PanDecryptionFailure.UnsupportedHeader);
    }

    [Fact]
    public void Rejects_a_header_with_duplicate_members()
    {
        // A parser that takes the first "alg" sees RSA1_5, one that takes the last sees RSA-OAEP-256.
        var header = $$"""{"alg":"RSA1_5","alg":"RSA-OAEP-256","enc":"A256GCM","kid":"{{TestJwe.DefaultKid}}"}""";

        AssertFails(TestJwe.Encrypt(Encoding.UTF8.GetBytes(Pan), _bankKey, header), PanDecryptionFailure.Malformed);
    }

    [Fact]
    public void Rejects_a_wrong_length_iv()
    {
        var jwe = TestJwe.ReplacePart(TestJwe.Encrypt(Pan, _bankKey), 2, iv => [.. iv, 0, 0, 0, 0]);

        AssertFails(jwe, PanDecryptionFailure.Malformed);
    }

    [Fact]
    public void Rejects_a_truncated_tag()
    {
        var jwe = TestJwe.ReplacePart(TestJwe.Encrypt(Pan, _bankKey), 4, tag => tag[..12]);

        AssertFails(jwe, PanDecryptionFailure.Malformed);
    }

    // ---- Algorithm allow-list ----

    [Theory]
    [InlineData("RSA1_5", "A256GCM")]         // PKCS#1 v1.5: Bleichenbacher
    [InlineData("RSA-OAEP", "A256GCM")]       // SHA-1 OAEP
    [InlineData("dir", "A256GCM")]
    [InlineData("A256KW", "A256GCM")]
    [InlineData("none", "A256GCM")]
    [InlineData("rsa-oaep-256", "A256GCM")]   // case matters
    [InlineData("RSA-OAEP-256", "A128GCM")]
    [InlineData("RSA-OAEP-256", "A256CBC-HS512")]
    [InlineData("RSA-OAEP-256", "")]
    public void Accepts_only_rsa_oaep_256_with_a256gcm(string alg, string enc)
    {
        var header = $$"""{"alg":"{{alg}}","enc":"{{enc}}","kid":"{{TestJwe.DefaultKid}}"}""";

        AssertFails(TestJwe.Encrypt(Encoding.UTF8.GetBytes(Pan), _bankKey, header), PanDecryptionFailure.UnsupportedHeader);
    }

    [Fact]
    public void Rejects_a_header_without_alg_or_enc()
    {
        var header = $$"""{"kid":"{{TestJwe.DefaultKid}}"}""";

        AssertFails(TestJwe.Encrypt(Encoding.UTF8.GetBytes(Pan), _bankKey, header), PanDecryptionFailure.UnsupportedHeader);
    }

    [Fact]
    public void Rejects_alg_given_as_a_non_string()
    {
        var header = $$"""{"alg":["RSA-OAEP-256"],"enc":"A256GCM","kid":"{{TestJwe.DefaultKid}}"}""";

        AssertFails(TestJwe.Encrypt(Encoding.UTF8.GetBytes(Pan), _bankKey, header), PanDecryptionFailure.UnsupportedHeader);
    }

    [Theory]
    [InlineData("\"zip\":\"DEF\"")]
    [InlineData("\"crit\":[\"exp\"],\"exp\":1")]
    public void Rejects_compression_and_critical_extensions(string extra)
    {
        var header = $$"""{"alg":"RSA-OAEP-256","enc":"A256GCM","kid":"{{TestJwe.DefaultKid}}",{{extra}}}""";

        AssertFails(TestJwe.Encrypt(Encoding.UTF8.GetBytes(Pan), _bankKey, header), PanDecryptionFailure.UnsupportedHeader);
    }

    // ---- Keys ----

    [Fact]
    public void Rejects_an_unknown_kid()
    {
        AssertFails(TestJwe.Encrypt(Pan, _bankKey, kid: "someone-elses-key"), PanDecryptionFailure.UnknownKey);
    }

    [Fact]
    public void Rejects_a_header_without_kid()
    {
        const string header = """{"alg":"RSA-OAEP-256","enc":"A256GCM"}""";

        AssertFails(TestJwe.Encrypt(Encoding.UTF8.GetBytes(Pan), _bankKey, header), PanDecryptionFailure.UnknownKey);
    }

    [Fact]
    public void Rejects_a_jwe_encrypted_for_a_different_key_under_our_kid()
    {
        using var otherKey = RSA.Create(3072);   // same size as ours, so it gets past the length checks

        AssertFails(TestJwe.Encrypt(Pan, otherKey), PanDecryptionFailure.DecryptionFailed);
    }

    [Fact]
    public void Rejects_a_key_wrapped_with_sha1_oaep_while_claiming_sha256()
    {
        var jwe = TestJwe.Encrypt(Encoding.UTF8.GetBytes(Pan), _bankKey, TestJwe.DefaultHeader(),
            keyWrapPadding: RSAEncryptionPadding.OaepSHA1);

        AssertFails(jwe, PanDecryptionFailure.DecryptionFailed);
    }

    [Fact]
    public void Rejects_a_content_key_of_the_wrong_size()
    {
        // A128GCM-sized key under an A256GCM header.
        var jwe = TestJwe.Encrypt(Encoding.UTF8.GetBytes(Pan), _bankKey, TestJwe.DefaultHeader(), cekSize: 16);

        AssertFails(jwe, PanDecryptionFailure.DecryptionFailed);
    }

    // ---- Integrity ----

    [Theory]
    [InlineData(1)]   // encrypted key
    [InlineData(2)]   // iv
    [InlineData(3)]   // ciphertext
    [InlineData(4)]   // tag
    public void Rejects_any_tampered_part(int part)
    {
        var jwe = TestJwe.ReplacePart(TestJwe.Encrypt(Pan, _bankKey), part, TestJwe.FlipFirstBit);

        AssertFails(jwe, PanDecryptionFailure.DecryptionFailed);
    }

    [Fact]
    public void Rejects_a_header_changed_after_encryption_because_it_is_authenticated()
    {
        var jwe = TestJwe.Encrypt(Pan, _bankKey);
        var parts = jwe.Split('.');
        // Same meaning, different bytes: still has to fail, the AAD is the header exactly as sent.
        parts[0] = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(
            $$"""{"alg":"RSA-OAEP-256","enc":"A256GCM","kid":"{{TestJwe.DefaultKid}}","typ":"JOSE"}"""));

        AssertFails(string.Join('.', parts), PanDecryptionFailure.DecryptionFailed);
    }

    // ---- Plaintext ----

    [Theory]
    [InlineData("4111111111111112")]        // fails Luhn
    [InlineData("41111111111")]             // 11 digits
    [InlineData("41111111111111111111")]    // 20 digits
    [InlineData("4111 1111 1111 1111")]     // spaces
    [InlineData("4111-1111-1111-1111")]
    [InlineData("411111111111111١")]        // Arabic-Indic digit one
    [InlineData("{\"pan\":\"4111111111111111\"}")]
    public void Rejects_plaintext_that_is_not_a_valid_pan(string plaintext)
    {
        var jwe = TestJwe.Encrypt(Encoding.UTF8.GetBytes(plaintext), _bankKey, TestJwe.DefaultHeader());

        AssertFails(jwe, PanDecryptionFailure.Malformed, PanDecryptionFailure.InvalidPan);
    }

    [Fact]
    public void A_luhn_failure_is_reported_as_invalid_pan_not_as_a_crypto_failure()
    {
        AssertFails(TestJwe.Encrypt("4111111111111112", _bankKey), PanDecryptionFailure.InvalidPan);
    }

    // ---- No leaks ----

    [Fact]
    public void Failure_exceptions_never_contain_the_pan_or_input()
    {
        var jwe = TestJwe.Encrypt("4111111111111112", _bankKey);

        var ex = Assert.Throws<PanDecryptionException>(() => _decryptor.Decrypt(jwe));

        Assert.DoesNotContain("4111111111111112", ex.ToString());
        Assert.DoesNotContain(jwe, ex.ToString());
        Assert.Null(ex.InnerException);
    }

    private void AssertFails(string jwe, params PanDecryptionFailure[] acceptable)
    {
        var ex = Assert.Throws<PanDecryptionException>(() => _decryptor.Decrypt(jwe));
        Assert.Contains(ex.Reason, acceptable);
        Assert.Equal("The encrypted PAN could not be decrypted.", ex.Message);
    }

    private static PanKeyRing KeyRingWith(params (string Kid, RSA Key)[] keys) =>
        new(new PanEncryptionOptions
        {
            Keys = keys.Select(k => new PanKeyOptions { Kid = k.Kid, PrivateKeyPem = k.Key.ExportPkcs8PrivateKeyPem() }).ToList(),
        }, contentRoot: AppContext.BaseDirectory);
}
