using System.ComponentModel.DataAnnotations;

namespace CardControl.Api.Downstream;

public sealed class DownstreamServiceOptions
{
    [Required, Url]
    public string BaseUrl { get; set; } = "";

    /// <summary>Overall time allowed for one call, after which the caller gets a 504.</summary>
    [Range(1, 60)]
    public int TimeoutSeconds { get; set; } = 5;
}

public static class DownstreamServices
{
    public const string SectionName = "Downstream";
    public const string Tokenisation = nameof(Tokenisation);
    public const string CardPlatform = nameof(CardPlatform);
}
