namespace CatalogPilot.Options;

public sealed class ImageClassifierOptions
{
    public const string SectionName = "ImageClassifier";

    public string ApiBaseUrl { get; set; } = "https://api.openai.com/v1";

    public string ApiKey { get; set; } = string.Empty;

    public string Model { get; set; } = "gpt-4.1-mini";

    public int MaxImages { get; set; } = 4;
}
