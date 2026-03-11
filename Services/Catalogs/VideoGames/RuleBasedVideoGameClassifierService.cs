using System.Text.RegularExpressions;
using CatalogPilot.Models;

namespace CatalogPilot.Services;

public sealed partial class RuleBasedVideoGameClassifierService : IVideoGameClassifierService
{
    private static readonly Dictionary<string, string[]> PlatformKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Nintendo Switch"] = ["switch", "nintendo switch"],
        ["PlayStation 5"] = ["ps5", "playstation 5"],
        ["PlayStation 4"] = ["ps4", "playstation 4"],
        ["Xbox Series X"] = ["xbox series", "series x"],
        ["Xbox One"] = ["xbox one"],
        ["Nintendo Wii"] = ["wii"],
        ["Nintendo GameCube"] = ["gamecube"],
        ["Nintendo 64"] = ["n64", "nintendo 64"],
        ["SNES"] = ["snes", "super nintendo"],
        ["NES"] = ["nes", "nintendo entertainment system"]
    };

    private static readonly string[] FranchiseKeywords =
    [
        "mario",
        "zelda",
        "pokemon",
        "sonic",
        "metroid",
        "halo",
        "call of duty",
        "final fantasy",
        "resident evil"
    ];

    private static readonly string[] EditionKeywords =
    [
        "collector",
        "limited",
        "game of the year",
        "greatest hits",
        "platinum hits",
        "special edition"
    ];

    public Task<ClassificationResult> ClassifyAsync(ListingInput input, CancellationToken cancellationToken = default)
    {
        var searchable = string.Join(
            ' ',
            [
                input.ItemName,
                input.Description,
                input.Platform,
                .. input.Photos.Select(p => p.FileName)
            ]).ToLowerInvariant();

        var platform = string.IsNullOrWhiteSpace(input.Platform)
            ? InferPlatform(searchable)
            : input.Platform;
        var franchise = InferKeyword(searchable, FranchiseKeywords);
        var edition = InferKeyword(searchable, EditionKeywords);
        var suggestedCondition = InferCondition(input, searchable);
        var suggestedTitle = BuildTitle(input.ItemName, platform, edition, input.IsSealed);
        var confidence = ComputeConfidence(input, platform, franchise, edition);

        var specifics = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Platform"] = string.IsNullOrWhiteSpace(platform) ? "Unknown" : platform,
            ["Item Type"] = "Video Game",
            ["Region Code"] = "NTSC-U/C (US/Canada)"
        };
        if (!string.IsNullOrWhiteSpace(franchise))
        {
            specifics["Franchise"] = franchise;
        }

        return Task.FromResult(new ClassificationResult
        {
            Source = "Rule-based",
            SuggestedTitle = suggestedTitle,
            SuggestedPlatform = platform,
            SuggestedCondition = suggestedCondition,
            Franchise = franchise,
            Edition = edition,
            CategoryId = "139973",
            Confidence = confidence,
            ItemSpecifics = specifics
        });
    }

    private static string InferPlatform(string searchable)
    {
        foreach (var (platform, keywords) in PlatformKeywords)
        {
            if (keywords.Any(searchable.Contains))
            {
                return platform;
            }
        }

        return string.Empty;
    }

    private static string InferKeyword(string searchable, IEnumerable<string> keywords)
    {
        return keywords.FirstOrDefault(searchable.Contains, string.Empty);
    }

    private static string InferCondition(ListingInput input, string searchable)
    {
        if (!string.IsNullOrWhiteSpace(input.Condition))
        {
            return input.Condition;
        }

        if (input.IsSealed || searchable.Contains("sealed", StringComparison.OrdinalIgnoreCase) || searchable.Contains("new", StringComparison.OrdinalIgnoreCase))
        {
            return "New";
        }

        if (searchable.Contains("for parts", StringComparison.OrdinalIgnoreCase))
        {
            return "For parts or not working";
        }

        return "Used";
    }

    private static string BuildTitle(string currentTitle, string platform, string edition, bool isSealed)
    {
        var baseTitle = string.IsNullOrWhiteSpace(currentTitle) ? "Video Game" : CleanSpacesRegex().Replace(currentTitle, " ").Trim();
        if (!string.IsNullOrWhiteSpace(platform) && !baseTitle.Contains(platform, StringComparison.OrdinalIgnoreCase))
        {
            baseTitle = $"{baseTitle} ({platform})";
        }

        if (!string.IsNullOrWhiteSpace(edition) && !baseTitle.Contains(edition, StringComparison.OrdinalIgnoreCase))
        {
            baseTitle = $"{baseTitle} {ToTitleCase(edition)}";
        }

        if (isSealed && !baseTitle.Contains("Sealed", StringComparison.OrdinalIgnoreCase))
        {
            baseTitle = $"{baseTitle} Sealed";
        }

        return baseTitle;
    }

    private static decimal ComputeConfidence(ListingInput input, string platform, string franchise, string edition)
    {
        decimal confidence = 0.25m;
        if (input.Photos.Count > 0)
        {
            confidence += 0.15m;
        }

        if (!string.IsNullOrWhiteSpace(input.ItemName))
        {
            confidence += 0.2m;
        }

        if (!string.IsNullOrWhiteSpace(platform))
        {
            confidence += 0.2m;
        }

        if (!string.IsNullOrWhiteSpace(franchise))
        {
            confidence += 0.1m;
        }

        if (!string.IsNullOrWhiteSpace(edition))
        {
            confidence += 0.1m;
        }

        return decimal.Min(confidence, 0.95m);
    }

    private static string ToTitleCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(word => char.ToUpperInvariant(word[0]) + word[1..]));
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex CleanSpacesRegex();
}
