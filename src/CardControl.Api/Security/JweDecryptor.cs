using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CardControl.Api.Security;

public enum PanDecryptionFailure
{
    /// <summary>Not a compact JWE: wrong number of parts, bad base64url, bad header JSON, wrong lengths.</summary>
    Malformed,
    /// <summary>Header asks for anything other than alg RSA-OAEP-256 / enc A256GCM, or uses zip / crit.</summary>
    UnsupportedHeader,
    /// <summary>No <c>kid</c>, or a <c>kid</c> the bank doesn't hold.</summary>
    UnknownKey,
    /// <summary>Key unwrap or authenticated decryption failed: wrong key, or tampered with.</summary>
    DecryptionFailed,
    /// <summary>Decrypted fine, but the plaintext isn't a valid PAN.</summary>
    InvalidPan,
}

/// <summary>
/// Why a PAN couldn't be decrypted. The message is fixed and never contains request data; the specific
/// <see cref="Reason"/> is for logs and metrics only. Callers get one generic error for all
/// cryptographic failures, so the response can't be used as an oracle.
/// </summary>
public sealed class PanDecryptionException(PanDecryptionFailure reason)
    : Exception("The encrypted PAN could not be decrypted.")
{
    public PanDecryptionFailure Reason { get; } = reason;
}

public interface IPanDecryptor
{
    /// <exception cref="PanDecryptionException">Whenever the input isn't a valid encrypted PAN.</exception>
    Pan Decrypt(string compactJwe);
}

/// <summary>
/// Decrypts a compact JWE (RFC 7516) carrying a PAN, accepting exactly what the partner is agreed to
/// send: alg RSA-OAEP-256, enc A256GCM, a <c>kid</c> naming one of the bank's keys, plaintext = PAN digits.
/// Hand-rolled on System.Security.Cryptography rather than a JWT library: the payload isn't JSON
/// (so JWT handlers reject it), and a strict allow-list is simpler to verify than a library's options.
/// </summary>
public sealed class JweDecryptor(PanKeyRing keys) : IPanDecryptor
{
    public const string Algorithm = "RSA-OAEP-256";
    public const string Encryption = "A256GCM";

    // Generous upper bound: RSA-4096 encrypted key + a 19-digit PAN is ~1 KB. Caps work done on junk input.
    private const int MaxCompactLength = 4096;
    private const int CekSizeBytes = 32;   // A256GCM
    private const int IvSizeBytes = 12;    // 96-bit GCM nonce
    private const int TagSizeBytes = 16;   // 128-bit GCM tag

    private static readonly JsonDocumentOptions HeaderJsonOptions = new() { AllowDuplicateProperties = false, MaxDepth = 4 };

    public Pan Decrypt(string compactJwe)
    {
        if (string.IsNullOrEmpty(compactJwe) || compactJwe.Length > MaxCompactLength)
            throw Fail(PanDecryptionFailure.Malformed);

        // header.encryptedKey.iv.ciphertext.tag
        var parts = compactJwe.Split('.');
        if (parts.Length != 5)
            throw Fail(PanDecryptionFailure.Malformed);

        var encodedHeader = parts[0];
        var kid = ReadAndCheckHeader(encodedHeader);

        if (!keys.TryGetKey(kid, out var rsa))
            throw Fail(PanDecryptionFailure.UnknownKey);

        var encryptedKey = DecodePart(parts[1]);
        var iv = DecodePart(parts[2]);
        var ciphertext = DecodePart(parts[3]);
        var tag = DecodePart(parts[4]);

        if (encryptedKey.Length != rsa!.KeySize / 8 || iv.Length != IvSizeBytes || tag.Length != TagSizeBytes
            || ciphertext.Length is < Pan.MinLength or > Pan.MaxLength)
            throw Fail(PanDecryptionFailure.Malformed);

        byte[]? cek = null;
        var plaintext = new byte[ciphertext.Length];
        try
        {
            try
            {
                cek = rsa.Decrypt(encryptedKey, RSAEncryptionPadding.OaepSHA256);
            }
            catch (CryptographicException)
            {
                throw Fail(PanDecryptionFailure.DecryptionFailed);
            }

            if (cek.Length != CekSizeBytes)
                throw Fail(PanDecryptionFailure.DecryptionFailed);

            try
            {
                using var aes = new AesGcm(cek, TagSizeBytes);
                // The protected header, exactly as sent, is the additional authenticated data (RFC 7516 §5.2).
                aes.Decrypt(iv, ciphertext, tag, plaintext, Encoding.ASCII.GetBytes(encodedHeader));
            }
            catch (CryptographicException) // includes AuthenticationTagMismatchException
            {
                throw Fail(PanDecryptionFailure.DecryptionFailed);
            }

            Span<char> digits = stackalloc char[plaintext.Length];
            for (var i = 0; i < plaintext.Length; i++)
                digits[i] = (char)plaintext[i];   // non-ASCII bytes become non-digits and fail validation

            var valid = Pan.TryCreate(digits, out var pan);
            digits.Clear();
            return valid ? pan! : throw Fail(PanDecryptionFailure.InvalidPan);
        }
        finally
        {
            if (cek is not null)
                CryptographicOperations.ZeroMemory(cek);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static string ReadAndCheckHeader(string encodedHeader)
    {
        JsonDocument header;
        try
        {
            // Duplicate members ("alg" twice) are refused: parsers disagree on which one wins.
            header = JsonDocument.Parse(DecodePart(encodedHeader), HeaderJsonOptions);
        }
        catch (JsonException)
        {
            throw Fail(PanDecryptionFailure.Malformed);
        }

        using (header)
        {
            var root = header.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw Fail(PanDecryptionFailure.Malformed);

            // Strict allow-list. Anything else (RSA1_5, dir, A128GCM, CBC-HS...) is refused, not negotiated.
            if (GetString(root, "alg") != Algorithm || GetString(root, "enc") != Encryption)
                throw Fail(PanDecryptionFailure.UnsupportedHeader);

            // Compression before encryption leaks plaintext length patterns; "crit" demands extensions we don't implement.
            if (root.TryGetProperty("zip", out _) || root.TryGetProperty("crit", out _))
                throw Fail(PanDecryptionFailure.UnsupportedHeader);

            var kid = GetString(root, "kid");
            return string.IsNullOrEmpty(kid) ? throw Fail(PanDecryptionFailure.UnknownKey) : kid;
        }
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static byte[] DecodePart(string part)
    {
        try
        {
            return Base64Url.DecodeFromChars(part);
        }
        catch (FormatException)
        {
            throw Fail(PanDecryptionFailure.Malformed);
        }
    }

    private static PanDecryptionException Fail(PanDecryptionFailure reason) => new(reason);
}

public static class PanDecryptionServiceExtensions
{
    public static IServiceCollection AddPanDecryption(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<PanEncryptionOptions>(configuration.GetSection(PanEncryptionOptions.SectionName));
        services.AddSingleton<PanKeyRing>();
        services.AddSingleton<IPanDecryptor, JweDecryptor>();
        return services;
    }
}
