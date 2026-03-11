using System.Text.Json;
using System.Text.RegularExpressions;
using CatalogPilot.Models;

namespace CatalogPilot.Services;

public sealed partial class LocalGameTitleBankService : IGameTitleBankService
{
    private readonly Lazy<IReadOnlyList<GameTitleBankEntry>> _lazyEntries;
    private readonly ILogger<LocalGameTitleBankService> _logger;

    public LocalGameTitleBankService(IWebHostEnvironment hostEnvironment, ILogger<LocalGameTitleBankService> logger)
    {
        _logger = logger;
        _lazyEntries = new Lazy<IReadOnlyList<GameTitleBankEntry>>(() => LoadEntries(hostEnvironment.ContentRootPath));
    }

    public Task<IReadOnlyList<GameTitleMatchResult>> SearchAsync(
        string query,
        string? platformHint = null,
        int maxResults = 8,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Task.FromResult<IReadOnlyList<GameTitleMatchResult>>([]);
        }

        var normalizedQuery = Normalize(query);
        var ocrNormalizedQuery = NormalizeOcrLike(normalizedQuery);
        var entries = _lazyEntries.Value;
        var matches = entries
            .Select(entry => new GameTitleMatchResult
            {
                Entry = entry,
                Score = ScoreEntry(normalizedQuery, ocrNormalizedQuery, Normalize(platformHint), entry)
            })
            .Where(m => m.Score >= 0.12m)
            .OrderByDescending(m => m.Score)
            .ThenBy(m => m.Entry.Title, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(maxResults, 1, 20))
            .ToArray();

        return Task.FromResult<IReadOnlyList<GameTitleMatchResult>>(matches);
    }

    public async Task<GameTitleMatchResult?> FindBestMatchAsync(
        string query,
        string? platformHint = null,
        CancellationToken cancellationToken = default)
    {
        var matches = await SearchAsync(query, platformHint, 1, cancellationToken);
        return matches.Count > 0 ? matches[0] : null;
    }

    private IReadOnlyList<GameTitleBankEntry> LoadEntries(string contentRootPath)
    {
        try
        {
            var path = Path.Combine(contentRootPath, "Data", "game-title-bank.json");
            if (!File.Exists(path))
            {
                _logger.LogWarning("Game title bank file missing at {Path}", path);
                return [];
            }

            var json = File.ReadAllText(path);
            var entries = JsonSerializer.Deserialize<List<GameTitleBankEntry>>(json, JsonOptions);
            return entries?.Where(e => !string.IsNullOrWhiteSpace(e.Title)).ToArray() ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load game title bank");
            return [];
        }
    }

    private static decimal ScoreEntry(
        string normalizedQuery,
        string ocrNormalizedQuery,
        string normalizedPlatformHint,
        GameTitleBankEntry entry)
    {
        if (string.IsNullOrWhiteSpace(normalizedQuery))
        {
            return 0m;
        }

        var title = Normalize(entry.Title);
        var platform = Normalize(entry.Platform);
        var aliases = entry.Aliases.Select(Normalize).Where(a => !string.IsNullOrWhiteSpace(a));
        var candidates = aliases.Prepend(title);

        var best = 0m;
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var ocrCandidate = NormalizeOcrLike(candidate);
            var score = 0m;
            if (candidate == normalizedQuery)
            {
                score += 0.75m;
            }
            else if (candidate.Contains(normalizedQuery, StringComparison.Ordinal))
            {
                score += 0.5m;
            }
            else if (normalizedQuery.Contains(candidate, StringComparison.Ordinal))
            {
                score += 0.42m;
            }

            score += TokenOverlapScore(normalizedQuery, candidate) * 0.4m;
            score += CharacterSimilarityScore(normalizedQuery, candidate) * 0.18m;
            score += FuzzyTokenSimilarityScore(ocrNormalizedQuery, ocrCandidate) * 0.55m;
            score += CharacterSimilarityScore(ocrNormalizedQuery, ocrCandidate) * 0.22m;
            best = decimal.Max(best, score);
        }

        if (!string.IsNullOrWhiteSpace(normalizedPlatformHint) && !string.IsNullOrWhiteSpace(platform))
        {
            if (platform == normalizedPlatformHint)
            {
                best += 0.12m;
            }
            else if (platform.Contains(normalizedPlatformHint, StringComparison.Ordinal) || normalizedPlatformHint.Contains(platform, StringComparison.Ordinal))
            {
                best += 0.06m;
            }
        }

        best += PlatformPatternBonus(normalizedQuery, platform);
        return decimal.Min(best, 1m);
    }

    private static decimal TokenOverlapScore(string query, string candidate)
    {
        var queryTokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var candidateTokens = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (queryTokens.Length == 0 || candidateTokens.Length == 0)
        {
            return 0m;
        }

        var overlapCount = queryTokens.Count(q => candidateTokens.Contains(q, StringComparer.Ordinal));
        return (decimal)overlapCount / decimal.Max(queryTokens.Length, candidateTokens.Length);
    }

    private static decimal CharacterSimilarityScore(string query, string candidate)
    {
        var queryBigrams = Bigrams(query);
        var candidateBigrams = Bigrams(candidate);
        if (queryBigrams.Count == 0 || candidateBigrams.Count == 0)
        {
            return 0m;
        }

        var intersection = queryBigrams.Intersect(candidateBigrams, StringComparer.Ordinal).Count();
        var union = queryBigrams.Union(candidateBigrams, StringComparer.Ordinal).Count();
        if (union == 0)
        {
            return 0m;
        }

        return (decimal)intersection / union;
    }

    private static decimal FuzzyTokenSimilarityScore(string query, string candidate)
    {
        var queryTokens = TokenizeForFuzzy(query);
        var candidateTokens = TokenizeForFuzzy(candidate);
        if (queryTokens.Length == 0 || candidateTokens.Length == 0)
        {
            return 0m;
        }

        decimal weightedScore = 0m;
        decimal totalWeight = 0m;
        foreach (var candidateToken in candidateTokens)
        {
            var tokenWeight = decimal.Max(1m, candidateToken.Length / 2m);
            totalWeight += tokenWeight;

            decimal bestSimilarity = 0m;
            foreach (var queryToken in queryTokens)
            {
                var similarity = TokenSimilarity(candidateToken, queryToken);
                if (similarity > bestSimilarity)
                {
                    bestSimilarity = similarity;
                }
            }

            weightedScore += bestSimilarity * tokenWeight;
        }

        if (totalWeight == 0m)
        {
            return 0m;
        }

        return weightedScore / totalWeight;
    }

    private static decimal TokenSimilarity(string a, string b)
    {
        if (a == b)
        {
            return 1m;
        }

        var maxLen = Math.Max(a.Length, b.Length);
        if (maxLen == 0)
        {
            return 0m;
        }

        if (Math.Abs(a.Length - b.Length) > 4)
        {
            return 0m;
        }

        var distance = LevenshteinDistance(a, b);
        if (distance > 4)
        {
            return 0m;
        }

        return 1m - (decimal)distance / maxLen;
    }

    private static int LevenshteinDistance(string a, string b)
    {
        if (a.Length == 0)
        {
            return b.Length;
        }

        if (b.Length == 0)
        {
            return a.Length;
        }

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    private static decimal PlatformPatternBonus(string normalizedQuery, string normalizedPlatform)
    {
        if (string.IsNullOrWhiteSpace(normalizedPlatform))
        {
            return 0m;
        }

        var compact = normalizedQuery.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (normalizedPlatform.Contains("playstation 3", StringComparison.Ordinal) &&
            Ps3LooseRegex().IsMatch(compact))
        {
            return 0.12m;
        }

        if (normalizedPlatform.Contains("playstation 4", StringComparison.Ordinal) &&
            Ps4LooseRegex().IsMatch(compact))
        {
            return 0.12m;
        }

        if (normalizedPlatform.Contains("playstation 5", StringComparison.Ordinal) &&
            Ps5LooseRegex().IsMatch(compact))
        {
            return 0.12m;
        }

        if (normalizedPlatform.Contains("xbox 360", StringComparison.Ordinal) &&
            Xbox360LooseRegex().IsMatch(compact))
        {
            return 0.12m;
        }

        return 0m;
    }

    private static HashSet<string> Bigrams(string value)
    {
        var stripped = value.Replace(" ", string.Empty, StringComparison.Ordinal);
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (stripped.Length < 2)
        {
            return set;
        }

        for (var i = 0; i < stripped.Length - 1; i++)
        {
            set.Add(stripped.Substring(i, 2));
        }

        return set;
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var cleaned = NonAlphaNumericRegex().Replace(value.ToLowerInvariant(), " ");
        return MultiSpaceRegex().Replace(cleaned, " ").Trim();
    }

    private static string NormalizeOcrLike(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var mapped = new string(value
            .Select(c => c switch
            {
                '0' => 'o',
                '1' => 'i',
                '2' => 'z',
                '3' => 'e',
                '4' => 'a',
                '5' => 's',
                '6' => 'g',
                '7' => 't',
                '8' => 'b',
                '9' => 'g',
                _ => c
            })
            .ToArray());
        return Normalize(mapped);
    }

    private static string[] TokenizeForFuzzy(string value)
    {
        return value
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length >= 2)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlphaNumericRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultiSpaceRegex();

    [GeneratedRegex(@"p[a-z0-9]{0,2}s[a-z0-9]{0,2}3")]
    private static partial Regex Ps3LooseRegex();

    [GeneratedRegex(@"p[a-z0-9]{0,2}s[a-z0-9]{0,2}4")]
    private static partial Regex Ps4LooseRegex();

    [GeneratedRegex(@"p[a-z0-9]{0,2}s[a-z0-9]{0,2}5")]
    private static partial Regex Ps5LooseRegex();

    [GeneratedRegex(@"x[a-z0-9]{0,2}b[a-z0-9]{0,2}o[a-z0-9]{0,2}x[a-z0-9]{0,2}3[a-z0-9]{0,2}6[a-z0-9]{0,2}0")]
    private static partial Regex Xbox360LooseRegex();
}
