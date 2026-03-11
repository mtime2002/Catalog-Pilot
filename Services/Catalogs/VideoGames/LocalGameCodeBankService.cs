using System.Text.Json;
using CatalogPilot.Models;

namespace CatalogPilot.Services;

public sealed class LocalGameCodeBankService : IGameCodeBankService
{
    private readonly Lazy<IReadOnlyList<GameCodeBankEntry>> _lazyEntries;
    private readonly ILogger<LocalGameCodeBankService> _logger;

    public LocalGameCodeBankService(IWebHostEnvironment hostEnvironment, ILogger<LocalGameCodeBankService> logger)
    {
        _logger = logger;
        _lazyEntries = new Lazy<IReadOnlyList<GameCodeBankEntry>>(() => LoadEntries(hostEnvironment.ContentRootPath));
    }

    public GameCodeMatchResult? FindBestMatch(IEnumerable<string> detectedCodes, string? platformHint = null)
    {
        var normalizedCodes = detectedCodes
            .Select(NormalizeCode)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (normalizedCodes.Length == 0)
        {
            return null;
        }

        var normalizedPlatformHint = NormalizeText(platformHint);
        var entries = _lazyEntries.Value;

        GameCodeMatchResult? best = null;
        foreach (var entry in entries)
        {
            var entryCodes = entry.Codes
                .Select(NormalizeCode)
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (entryCodes.Length == 0)
            {
                continue;
            }

            foreach (var detected in normalizedCodes)
            {
                foreach (var candidate in entryCodes)
                {
                    var score = ScoreCodeMatch(detected, candidate);
                    if (score <= 0m)
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(normalizedPlatformHint))
                    {
                        var normalizedEntryPlatform = NormalizeText(entry.Platform);
                        if (normalizedEntryPlatform == normalizedPlatformHint)
                        {
                            score += 0.08m;
                        }
                        else if (!string.IsNullOrWhiteSpace(normalizedEntryPlatform) &&
                                 (normalizedEntryPlatform.Contains(normalizedPlatformHint, StringComparison.Ordinal) ||
                                  normalizedPlatformHint.Contains(normalizedEntryPlatform, StringComparison.Ordinal)))
                        {
                            score += 0.04m;
                        }
                    }

                    score = decimal.Min(score, 1m);
                    if (best is null || score > best.Score)
                    {
                        best = new GameCodeMatchResult
                        {
                            Entry = entry,
                            MatchedCode = detected,
                            Score = score
                        };
                    }
                }
            }
        }

        return best;
    }

    private IReadOnlyList<GameCodeBankEntry> LoadEntries(string contentRootPath)
    {
        try
        {
            var path = Path.Combine(contentRootPath, "Data", "game-code-bank.json");
            if (!File.Exists(path))
            {
                _logger.LogWarning("Game code bank file missing at {Path}", path);
                return [];
            }

            var json = File.ReadAllText(path);
            var entries = JsonSerializer.Deserialize<List<GameCodeBankEntry>>(json, JsonOptions);
            return entries?
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Title) && entry.Codes.Length > 0)
                .ToArray() ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load game code bank");
            return [];
        }
    }

    private static decimal ScoreCodeMatch(string detectedCode, string candidateCode)
    {
        if (detectedCode == candidateCode)
        {
            return 1m;
        }

        if (detectedCode.Length >= 7 && candidateCode.Length >= 7)
        {
            if (detectedCode.Contains(candidateCode, StringComparison.Ordinal) ||
                candidateCode.Contains(detectedCode, StringComparison.Ordinal))
            {
                return 0.78m;
            }

            if (detectedCode[..4] == candidateCode[..4])
            {
                var suffixDetected = detectedCode[4..];
                var suffixCandidate = candidateCode[4..];
                if (suffixDetected == suffixCandidate)
                {
                    return 0.9m;
                }
            }
        }

        return 0m;
    }

    private static string NormalizeCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return string.Empty;
        }

        return new string(code
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());
    }

    private static string NormalizeText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var lowered = text.ToLowerInvariant();
        return string.Join(' ', lowered
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
}
