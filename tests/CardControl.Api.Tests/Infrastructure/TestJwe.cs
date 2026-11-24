using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace CardControl.Api.Tests.Infrastructure;

/// <summary>
/// The partner's side of PAN encryption, mirroring tools/jwe-tool.cs, with knobs so tests can produce
/// every kind of bad JWE as well as good ones.
/// </summary>
public static class TestJwe
{
    public const string DefaultKid = "bank-test-1";

    public static string DefaultHeader(string kid = DefaultKid) =>
        $$"""{"alg":"RSA-OAEP-256","enc":"A256GCM","kid":"{{kid}}"}""";

    public static string Encrypt(string pan, RSA publicKey, string kid = DefaultKid) =>
        Encrypt(Encoding.UTF8.GetBytes(pan), publicKey, DefaultHeader(kid));

    public static string Encrypt(byte[] plaintext, RSA publicKey, string protectedHeader,
        int cekSize = 32, RSAEncryptionPadding? keyWrapPadding = null)
    {
        var encodedHeader = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(protectedHeader));
        var cek = RandomNumberGenerator.GetBytes(cekSize);
        var iv = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];

        using (var aes = new AesGcm(cek, tagSizeInBytes: 16))
            aes.Encrypt(iv, plaintext, ciphertext, tag, associatedData: Encoding.ASCII.GetBytes(encodedHeader));

        var encryptedKey = publicKey.Encrypt(cek, keyWrapPadding ?? RSAEncryptionPadding.OaepSHA256);

        return string.Join('.',
            encodedHeader,
            Base64Url.EncodeToString(encryptedKey),
            Base64Url.EncodeToString(iv),
            Base64Url.EncodeToString(ciphertext),
            Base64Url.EncodeToString(tag));
    }

    /// <summary>Replaces one of the five parts (0 header, 1 key, 2 iv, 3 ciphertext, 4 tag).</summary>
    public static string ReplacePart(string jwe, int index, Func<byte[], byte[]> change)
    {
        var parts = jwe.Split('.');
        parts[index] = Base64Url.EncodeToString(change(Base64Url.DecodeFromChars(parts[index])));
        return string.Join('.', parts);
    }

    public static byte[] FlipFirstBit(byte[] bytes)
    {
        var copy = (byte[])bytes.Clone();
        copy[0] ^= 0x01;
        return copy;
    }
}
