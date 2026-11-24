using System.Text.Json;
using CardControl.Api.Security;
using Microsoft.Extensions.Logging;
using CardControl.Api.Tests.Infrastructure;

namespace CardControl.Api.Tests.Security;

public class PanTests
{
    private const string Digits = "4111111111111111";

    [Theory]
    [InlineData("4111111111111111")]
    [InlineData("378282246310005")]
    [InlineData("4000000000003220")]
    [InlineData("000000000000")]          // 12 digits, Luhn-valid
    [InlineData("4222222222222222224")]   // 19 digits, Luhn-valid
    public void Accepts_12_to_19_digits_passing_luhn(string candidate)
    {
        Assert.True(Pan.TryCreate(candidate, out var pan));
        Assert.Equal(candidate, pan!.RevealDigits());
    }

    [Theory]
    [InlineData("")]
    [InlineData("4111111111111112")]
    [InlineData("41111111111")]
    [InlineData("42222222222222222224")]
    [InlineData("4111x11111111111")]
    [InlineData(" 4111111111111111")]
    public void Rejects_anything_else(string candidate)
    {
        Assert.False(Pan.TryCreate(candidate, out var pan));
        Assert.Null(pan);
    }

    [Fact]
    public void ToString_and_interpolation_show_only_the_last_four()
    {
        var pan = Create(Digits);

        Assert.Equal("************1111", pan.ToString());
        Assert.Equal("card ************1111", $"card {pan}");
        Assert.Equal("1111", pan.LastFour);
    }

    [Fact]
    public void Json_serialisation_writes_the_masked_form()
    {
        var json = JsonSerializer.Serialize(new { pan = Create(Digits) });

        Assert.Equal("""{"pan":"************1111"}""", json);
    }

    [Fact]
    public void Json_deserialisation_is_refused()
    {
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize<Pan>($"\"{Digits}\""));
    }

    [Fact]
    public void Structured_logging_of_a_pan_records_the_masked_form()
    {
        using var logs = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs));

        factory.CreateLogger("test").LogInformation("Processing {Pan}", Create(Digits));

        Assert.Contains("************1111", logs.AllText);
        Assert.DoesNotContain(Digits, logs.AllText);
    }

    private static Pan Create(string digits) =>
        Pan.TryCreate(digits, out var pan) ? pan! : throw new ArgumentException("bad test PAN");
}
