using System.Text.Json;
using System.Text.Json.Serialization;

namespace CardControl.Api.Security;

/// <summary>
/// A primary account number (card number). Deliberately hard to leak: <see cref="ToString"/> and JSON
/// serialisation only ever produce the masked form, so a PAN that ends up in a log message, an
/// exception or a response by accident shows as <c>************1111</c>. The digits are only
/// reachable through <see cref="RevealDigits"/>, which should be called in exactly one place:
/// the request to the tokenisation service.
/// </summary>
[JsonConverter(typeof(MaskingJsonConverter))]
public sealed class Pan
{
    public const int MinLength = 12;
    public const int MaxLength = 19;

    private readonly string _digits;

    private Pan(string digits) => _digits = digits;

    /// <summary>Last four digits, masked: safe for logs.</summary>
    public string Masked => new string('*', _digits.Length - 4) + _digits[^4..];

    public string LastFour => _digits[^4..];

    public static bool TryCreate(ReadOnlySpan<char> candidate, out Pan? pan)
    {
        pan = IsValid(candidate) ? new Pan(candidate.ToString()) : null;
        return pan is not null;
    }

    /// <summary>12-19 ASCII digits passing the Luhn check (the same rule the tokenisation service applies).</summary>
    public static bool IsValid(ReadOnlySpan<char> candidate)
    {
        if (candidate.Length is < MinLength or > MaxLength)
            return false;

        var sum = 0;
        for (var i = 0; i < candidate.Length; i++)
        {
            var c = candidate[candidate.Length - 1 - i];
            if (!char.IsAsciiDigit(c))
                return false;

            var digit = c - '0';
            if (i % 2 == 1)
            {
                digit *= 2;
                if (digit > 9)
                    digit -= 9;
            }
            sum += digit;
        }
        return sum % 10 == 0;
    }

    /// <summary>The clear-text digits. Only for sending to the tokenisation service.</summary>
    public string RevealDigits() => _digits;

    public override string ToString() => Masked;

    private sealed class MaskingJsonConverter : JsonConverter<Pan>
    {
        public override Pan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException("PANs are never read from plain JSON; they arrive encrypted.");

        public override void Write(Utf8JsonWriter writer, Pan value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Masked);
    }
}
