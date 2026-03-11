using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CatalogPilot.Models;
using CatalogPilot.Options;
using Microsoft.Extensions.Options;

namespace CatalogPilot.Services;

public sealed class VisionAssistedVideoGameClassifierService : IVideoGameClassifierService
{
    private const int MaxImageBytes = 5 * 1024 * 1024;
    private readonly HttpClient _httpClient;
    private readonly ImageClassifierOptions _options;
    private readonly IWebHostEnvironment _hostEnvironment;
    private readonly RuleBasedVideoGameClassifierService _fallbackClassifier;
    private readonly ILogger<VisionAssistedVideoGameClassifierService> _logger;

    public VisionAssistedVideoGameClassifierService(
        HttpClient httpClient,
        IOptions<ImageClassifierOptions> options,
        IWebHostEnvironment hostEnvironment,
        RuleBasedVideoGameClassifierService fallbackClassifier,
        ILogger<VisionAssistedVideoGameClassifierService> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _hostEnvironment = hostEnvironment;
        _fallbackClassifier = fallbackClassifier;
        _logger = logger;
    }

    public async Task<ClassificationResult> ClassifyAsync(ListingInput input, CancellationToken cancellationToken = default)
    {
        var fallback = await _fallbackClassifier.ClassifyAsync(input, cancellationToken);
        if (string.IsNullOrWhiteSpace(_options.ApiKey) || input.Photos.Count == 0)
        {
            return fallback;
        }

        var imageDataUris = await LoadImageDataUrisAsync(input, cancellationToken);
        if (imageDataUris.Count == 0)
        {
            return fallback;
        }

        var vision = await TryClassifyWithVisionAsync(input, imageDataUris, cancellationToken);
        if (vision is null)
        {
            return fallback;
        }

        return MergeResults(input, fallback, vision);
    }

    private async Task<VisionClassification?> TryClassifyWithVisionAsync(
        ListingInput input,
        IReadOnlyList<string> imageDataUris,
        CancellationToken cancellationToken)
    {
        var userContent = new List<object>
        {
            new
            {
                type = "text",
                text = BuildVisionPrompt(input)
            }
        };

        foreach (var imageDataUri in imageDataUris)
        {
            userContent.Add(new
            {
                type = "image_url",
                image_url = new
                {
                    url = imageDataUri
                }
            });
        }

        var requestPayload = new
        {
            model = _options.Model,
            response_format = new
            {
                type = "json_object"
            },
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = "You identify video game box products from photos and output concise structured data for eBay listings."
                },
                new
                {
                    role = "user",
                    content = userContent
                }
            }
        };

        var endpoint = $"{_options.ApiBaseUrl.TrimEnd('/')}/chat/completions";
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(requestPayload), Encoding.UTF8, "application/json");

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Vision classification failed: {StatusCode} - {Body}", response.StatusCode, Truncate(responseBody, 360));
                return null;
            }

            var contentText = ExtractResponseContent(responseBody);
            if (string.IsNullOrWhiteSpace(contentText))
            {
                return null;
            }

            return ParseVisionClassification(contentText);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Vision classification request failed");
            return null;
        }
    }

    private async Task<IReadOnlyList<string>> LoadImageDataUrisAsync(ListingInput input, CancellationToken cancellationToken)
    {
        var dataUris = new List<string>();
        foreach (var photo in input.Photos.Take(Math.Max(1, _options.MaxImages)))
        {
            var absolutePath = ResolvePhotoPath(photo.RelativeUrl);
            if (string.IsNullOrWhiteSpace(absolutePath) || !File.Exists(absolutePath))
            {
                continue;
            }

            var fileInfo = new FileInfo(absolutePath);
            if (fileInfo.Length <= 0 || fileInfo.Length > MaxImageBytes)
            {
                continue;
            }

            var bytes = await File.ReadAllBytesAsync(absolutePath, cancellationToken);
            var mimeType = !string.IsNullOrWhiteSpace(photo.ContentType) && photo.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                ? photo.ContentType
                : GuessImageMimeType(absolutePath);

            dataUris.Add($"data:{mimeType};base64,{Convert.ToBase64String(bytes)}");
        }

        return dataUris;
    }

    private ClassificationResult MergeResults(ListingInput input, ClassificationResult fallback, VisionClassification vision)
    {
        var title = FirstNonEmpty(vision.Title, fallback.SuggestedTitle, input.ItemName);
        var platform = FirstNonEmpty(vision.Platform, fallback.SuggestedPlatform, input.Platform);
        var condition = FirstNonEmpty(vision.Condition, fallback.SuggestedCondition, input.Condition);
        var franchise = FirstNonEmpty(vision.Franchise, fallback.Franchise);
        var edition = FirstNonEmpty(vision.Edition, fallback.Edition);

        if (vision.IsSealed is true && !title.Contains("Sealed", StringComparison.OrdinalIgnoreCase))
        {
            title = $"{title} Sealed".Trim();
        }

        var specifics = new Dictionary<string, string>(fallback.ItemSpecifics, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in vision.ItemSpecifics)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            specifics[key] = value;
        }

        if (!string.IsNullOrWhiteSpace(platform))
        {
            specifics["Platform"] = platform;
        }

        return new ClassificationResult
        {
            Source = "Vision + Rule-based",
            SuggestedTitle = title,
            SuggestedPlatform = platform,
            SuggestedCondition = condition,
            Franchise = franchise,
            Edition = edition,
            CategoryId = fallback.CategoryId,
            Confidence = NormalizeConfidence(vision.Confidence, fallback.Confidence),
            ItemSpecifics = specifics
        };
    }

    private static string BuildVisionPrompt(ListingInput input)
    {
        return
            """
            Identify the video game from the supplied photos and return ONLY JSON.
            Required JSON fields:
            - title: string
            - platform: string
            - condition: one of "New", "Used", "For parts or not working"
            - franchise: string
            - edition: string
            - isSealed: boolean
            - confidence: number between 0 and 1
            - itemSpecifics: object with key-value pairs for eBay specifics

            Context from user:
            """
            + $"\nitemName={input.ItemName}\nplatform={input.Platform}\ndescription={input.Description}\ncondition={input.Condition}\nsealed={input.IsSealed}";
    }

    private static VisionClassification? ParseVisionClassification(string rawContent)
    {
        var json = ExtractJsonObject(rawContent);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var specifics = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("itemSpecifics", out var itemSpecificsElement) &&
                itemSpecificsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in itemSpecificsElement.EnumerateObject())
                {
                    specifics[prop.Name] = prop.Value.ToString();
                }
            }

            return new VisionClassification
            {
                Title = ReadString(root, "title", "suggestedTitle"),
                Platform = ReadString(root, "platform", "suggestedPlatform"),
                Condition = ReadString(root, "condition", "suggestedCondition"),
                Franchise = ReadString(root, "franchise"),
                Edition = ReadString(root, "edition"),
                IsSealed = ReadBool(root, "isSealed"),
                Confidence = ReadDecimal(root, "confidence"),
                ItemSpecifics = specifics
            };
        }
        catch
        {
            return null;
        }
    }

    private static string ExtractResponseContent(string responseBody)
    {
        using var doc = JsonDocument.Parse(responseBody);
        if (!doc.RootElement.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0)
        {
            return string.Empty;
        }

        var firstChoice = choices[0];
        if (!firstChoice.TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var content))
        {
            return string.Empty;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? string.Empty;
        }

        if (content.ValueKind == JsonValueKind.Array)
        {
            var textParts = content.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("text", out _))
                .Select(e => e.GetProperty("text").GetString())
                .Where(t => !string.IsNullOrWhiteSpace(t));
            return string.Join('\n', textParts!);
        }

        return string.Empty;
    }

    private string ResolvePhotoPath(string relativeUrl)
    {
        if (string.IsNullOrWhiteSpace(relativeUrl))
        {
            return string.Empty;
        }

        var normalized = relativeUrl.Replace('\\', '/').TrimStart('/');
        if (normalized.Contains("..", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return Path.Combine(_hostEnvironment.WebRootPath, normalized);
    }

    private static string GuessImageMimeType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => "image/jpeg"
        };
    }

    private static string ReadString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var value))
            {
                continue;
            }

            var text = value.ToString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return string.Empty;
    }

    private static bool? ReadBool(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static decimal? ReadDecimal(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var numeric))
        {
            return numeric;
        }

        return decimal.TryParse(value.ToString(), out var parsed) ? parsed : null;
    }

    private static decimal NormalizeConfidence(decimal? candidate, decimal fallback)
    {
        if (!candidate.HasValue)
        {
            return fallback;
        }

        var bounded = decimal.Max(0m, decimal.Min(candidate.Value, 1m));
        return decimal.Max(bounded, fallback);
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static string ExtractJsonObject(string text)
    {
        var trimmed = text.Trim();
        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return string.Empty;
        }

        return trimmed[start..(end + 1)];
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private sealed class VisionClassification
    {
        public string Title { get; init; } = string.Empty;

        public string Platform { get; init; } = string.Empty;

        public string Condition { get; init; } = string.Empty;

        public string Franchise { get; init; } = string.Empty;

        public string Edition { get; init; } = string.Empty;

        public bool? IsSealed { get; init; }

        public decimal? Confidence { get; init; }

        public Dictionary<string, string> ItemSpecifics { get; init; } = [];
    }
}
