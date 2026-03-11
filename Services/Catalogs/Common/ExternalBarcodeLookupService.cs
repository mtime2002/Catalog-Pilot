using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using CatalogPilot.Models;
using CatalogPilot.Options;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace CatalogPilot.Services;

public sealed partial class ExternalBarcodeLookupService : IExternalBarcodeLookupService
{
    private const decimal TitleBankCanonicalizationMinScore = 0.56m;
    private const decimal BarcodeLookupMinConfidence = 0.70m;
    private readonly HttpClient _httpClient;
    private readonly IGameTitleBankService _titleBankService;
    private readonly IMemoryCache _cache;
    private readonly ExternalBarcodeLookupOptions _options;
    private readonly ILogger<ExternalBarcodeLookupService> _logger;

    public ExternalBarcodeLookupService(
        HttpClient httpClient,
        IGameTitleBankService titleBankService,
        IMemoryCache cache,
        IOptions<ExternalBarcodeLookupOptions> options,
        ILogger<ExternalBarcodeLookupService> logger)
    {
        _httpClient = httpClient;
        _titleBankService = titleBankService;
        _cache = cache;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ExternalBarcodeLookupResult?> LookupAsync(
        string code,
        string? platformHint = null,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return null;
        }

        var normalizedCode = NormalizeCode(code);
        if (normalizedCode.Length < 7)
        {
            return null;
        }

        var normalizedPlatformHint = NormalizeText(platformHint);
        var cacheKey = $"ext-barcode:{normalizedCode}|{normalizedPlatformHint}";
        if (_cache.TryGetValue(cacheKey, out CachedLookupResult? cached) && cached is not null)
        {
            return cached.Result;
        }

        var candidates = await LookupCandidatesCoreAsync(normalizedCode, platformHint, 6, cancellationToken);
        var result = candidates.FirstOrDefault(candidate => candidate.Confidence >= BarcodeLookupMinConfidence);
        if (result is null && candidates.Count > 0)
        {
            _logger.LogDebug(
                "Discarding low-confidence barcode lookup for {Code}. Top confidence={Confidence}",
                normalizedCode,
                candidates[0].Confidence);
        }

        var ttl = TimeSpan.FromMinutes(Math.Clamp(_options.CacheMinutes, 1, 360));
        _cache.Set(cacheKey, new CachedLookupResult { Result = result }, ttl);
        return result;
    }

    public async Task<IReadOnlyList<ExternalBarcodeLookupResult>> LookupCandidatesAsync(
        string code,
        string? platformHint = null,
        int maxResults = 5,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return [];
        }

        var normalizedCode = NormalizeCode(code);
        if (normalizedCode.Length < 7)
        {
            return [];
        }

        var requestedMax = Math.Clamp(maxResults, 1, 12);
        var normalizedPlatformHint = NormalizeText(platformHint);
        var cacheKey = $"ext-barcode-candidates:{normalizedCode}|{normalizedPlatformHint}|{requestedMax}";
        if (_cache.TryGetValue(cacheKey, out ExternalBarcodeLookupResult[]? cached) && cached is not null)
        {
            return cached;
        }

        var results = await LookupCandidatesCoreAsync(normalizedCode, platformHint, requestedMax, cancellationToken);
        var ttl = TimeSpan.FromMinutes(Math.Clamp(_options.CacheMinutes, 1, 360));
        var snapshot = results.ToArray();
        _cache.Set(cacheKey, snapshot, ttl);
        return snapshot;
    }

    public async Task<ExternalBarcodeLookupResult?> LookupByTitleAsync(
        string title,
        string? platformHint = null,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return null;
        }

        var normalizedTitle = NormalizeText(title);
        if (string.IsNullOrWhiteSpace(normalizedTitle) || normalizedTitle.Length < 4)
        {
            return null;
        }

        var normalizedPlatformHint = NormalizeText(platformHint);
        var cacheKey = $"ext-barcode-title:{normalizedTitle}|{normalizedPlatformHint}";
        if (_cache.TryGetValue(cacheKey, out CachedLookupResult? cached) && cached is not null)
        {
            return cached.Result;
        }

        var result = await LookupByTitleCoreAsync(title, platformHint, cancellationToken);
        var ttl = TimeSpan.FromMinutes(Math.Clamp(_options.CacheMinutes, 1, 360));
        _cache.Set(cacheKey, new CachedLookupResult { Result = result }, ttl);
        return result;
    }

    private async Task<ExternalBarcodeLookupResult?> LookupByTitleCoreAsync(
        string title,
        string? platformHint,
        CancellationToken cancellationToken)
    {
        var candidate = await TryUpcItemDbByTitleAsync(title, platformHint, cancellationToken);
        if (candidate is null)
        {
            return null;
        }

        var enriched = await EnrichWithMobyAsync(candidate, platformHint, cancellationToken);
        return await CanonicalizeWithTitleBankAsync(enriched, platformHint, cancellationToken);
    }

    private async Task<ExternalBarcodeLookupResult?> LookupCoreAsync(
        string normalizedCode,
        string? platformHint,
        CancellationToken cancellationToken)
    {
        var candidates = await LookupCandidatesCoreAsync(normalizedCode, platformHint, 1, cancellationToken);
        return candidates.FirstOrDefault();
    }

    private async Task<IReadOnlyList<ExternalBarcodeLookupResult>> LookupCandidatesCoreAsync(
        string normalizedCode,
        string? platformHint,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(maxResults, 1, 12);
        var rawCandidates = new List<ExternalBarcodeLookupResult>(12);

        // MobyGames has no UPC endpoint, but strict direct title lookup can sometimes rescue full-code queries.
        var mobyDirect = await TryMobyByTitleAsync(normalizedCode, platformHint, strictTitleScore: true, cancellationToken);
        if (mobyDirect is not null)
        {
            rawCandidates.Add(mobyDirect with { Code = normalizedCode, Provider = "MobyGames" });
        }

        var upcItemDbCandidates = await TryUpcItemDbCandidatesAsync(
            normalizedCode,
            platformHint,
            maxResults: 8,
            cancellationToken);
        rawCandidates.AddRange(upcItemDbCandidates);

        var goUpcCandidate = await TryGoUpcAsync(normalizedCode, platformHint, cancellationToken);
        if (goUpcCandidate is not null)
        {
            rawCandidates.Add(goUpcCandidate);
        }

        var eanCandidate = await TryEanDbAsync(normalizedCode, platformHint, cancellationToken);
        if (eanCandidate is not null)
        {
            rawCandidates.Add(eanCandidate);
        }

        if (rawCandidates.Count == 0)
        {
            return [];
        }

        var merged = new Dictionary<string, ExternalBarcodeLookupResult>(StringComparer.Ordinal);
        foreach (var raw in rawCandidates.Take(16))
        {
            var enriched = await EnrichWithMobyAsync(raw, platformHint, cancellationToken);
            var canonical = await CanonicalizeWithTitleBankAsync(enriched, platformHint, cancellationToken) ?? enriched;
            var normalizedTitle = NormalizeText(canonical.Title);
            if (string.IsNullOrWhiteSpace(normalizedTitle))
            {
                continue;
            }

            var normalizedPlatform = NormalizeText(canonical.Platform);
            var key = $"{normalizedTitle}|{normalizedPlatform}";
            if (!merged.TryGetValue(key, out var existing))
            {
                merged[key] = canonical with { Code = normalizedCode };
                continue;
            }

            if (canonical.Confidence > existing.Confidence)
            {
                merged[key] = MergeProviderInfo(canonical with { Code = normalizedCode }, existing);
                continue;
            }

            merged[key] = MergeProviderInfo(existing, canonical);
        }

        return merged.Values
            .OrderByDescending(candidate => candidate.Confidence)
            .ThenBy(candidate => candidate.Title, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToArray();
    }

    private async Task<ExternalBarcodeLookupResult?> TryUpcItemDbAsync(
        string normalizedCode,
        string? platformHint,
        CancellationToken cancellationToken)
    {
        var candidates = await TryUpcItemDbCandidatesAsync(
            normalizedCode,
            platformHint,
            maxResults: 1,
            cancellationToken);
        return candidates.FirstOrDefault();
    }

    private async Task<IReadOnlyList<ExternalBarcodeLookupResult>> TryUpcItemDbCandidatesAsync(
        string normalizedCode,
        string? platformHint,
        int maxResults,
        CancellationToken cancellationToken)
    {
        if (!_options.UpcItemDb.Enabled)
        {
            return [];
        }

        var baseUrl = _options.UpcItemDb.ApiBaseUrl.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return [];
        }

        var userKey = _options.UpcItemDb.UserKey.Trim();
        var useTrial = string.IsNullOrWhiteSpace(userKey) && _options.UpcItemDb.UseTrialWithoutKey;
        if (!useTrial && string.IsNullOrWhiteSpace(userKey))
        {
            return [];
        }

        var endpoint = useTrial
            ? $"{baseUrl}/prod/trial/lookup?upc={Uri.EscapeDataString(normalizedCode)}"
            : $"{baseUrl}/prod/v1/lookup?upc={Uri.EscapeDataString(normalizedCode)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        ApplyCommonHeaders(request);
        if (!useTrial)
        {
            request.Headers.TryAddWithoutValidation("user_key", userKey);
            request.Headers.TryAddWithoutValidation("key_type", _options.UpcItemDb.KeyType.Trim());
        }

        try
        {
            using var response = await SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var candidates = new List<ExternalBarcodeLookupResult>();
            var normalizedHint = NormalizeText(platformHint);
            foreach (var item in items.EnumerateArray())
            {
                var title = NormalizeWhitespace(GetString(item, "title"));
                if (string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                var category = NormalizeWhitespace(GetString(item, "category"));
                var brand = NormalizeWhitespace(GetString(item, "brand"));
                if (!LooksLikeVideoGame(title, category, platformHint))
                {
                    continue;
                }

                var inferredPlatform = InferPlatform(title, category, platformHint);
                var titleBankMatch = await _titleBankService.FindBestMatchAsync(
                    title,
                    string.IsNullOrWhiteSpace(inferredPlatform) ? platformHint : inferredPlatform,
                    cancellationToken);
                var titleBankScore = titleBankMatch?.Score ?? 0m;

                var confidence = ScoreUpcItemDbCandidate(
                    title,
                    category,
                    inferredPlatform,
                    normalizedHint,
                    titleBankScore);
                if (confidence < 0.52m)
                {
                    continue;
                }

                var canonicalTitle = title;
                var canonicalPlatform = inferredPlatform;
                var franchise = string.Empty;
                if (titleBankMatch is not null &&
                    titleBankMatch.Score >= 0.64m &&
                    !HasConflictingNumericTokens(title, titleBankMatch.Entry.Title))
                {
                    canonicalTitle = titleBankMatch.Entry.Title;
                    if (!string.IsNullOrWhiteSpace(titleBankMatch.Entry.Platform))
                    {
                        canonicalPlatform = titleBankMatch.Entry.Platform;
                    }

                    franchise = titleBankMatch.Entry.Franchise;
                    confidence = decimal.Min(0.92m, confidence + (titleBankMatch.Score * 0.08m));
                }

                var candidate = new ExternalBarcodeLookupResult
                {
                    Code = normalizedCode,
                    Title = canonicalTitle,
                    Platform = canonicalPlatform,
                    Franchise = franchise,
                    Provider = "UPCitemdb",
                    Brand = brand,
                    Category = category,
                    Confidence = confidence
                };

                candidates.Add(candidate);
            }

            return candidates
                .OrderByDescending(candidate => candidate.Confidence)
                .ThenBy(candidate => candidate.Title, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Clamp(maxResults, 1, 12))
                .ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "UPCitemdb lookup failed for code {Code}", normalizedCode);
            return [];
        }
    }

    private async Task<ExternalBarcodeLookupResult?> TryGoUpcAsync(
        string normalizedCode,
        string? platformHint,
        CancellationToken cancellationToken)
    {
        if (!_options.GoUpc.Enabled)
        {
            return null;
        }

        var baseUrl = _options.GoUpc.ApiBaseUrl.TrimEnd('/');
        var key = _options.GoUpc.ApiKey.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var endpoint = $"{baseUrl}/code/{Uri.EscapeDataString(normalizedCode)}";
        if (!_options.GoUpc.UseBearerAuth)
        {
            endpoint = $"{endpoint}?key={Uri.EscapeDataString(key)}";
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        ApplyCommonHeaders(request);
        if (_options.GoUpc.UseBearerAuth)
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
        }

        try
        {
            using var response = await SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);

            var product = doc.RootElement.TryGetProperty("product", out var productElement)
                ? productElement
                : doc.RootElement;

            var title = NormalizeWhitespace(GetString(product, "name"));
            if (string.IsNullOrWhiteSpace(title))
            {
                title = NormalizeWhitespace(GetString(doc.RootElement, "name"));
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            var category = NormalizeWhitespace(GetString(product, "category"));
            var brand = NormalizeWhitespace(GetString(product, "brand"));
            if (!LooksLikeVideoGame(title, category, platformHint))
            {
                return null;
            }

            return new ExternalBarcodeLookupResult
            {
                Code = normalizedCode,
                Title = title,
                Platform = InferPlatform(title, category, platformHint),
                Provider = "Go-UPC",
                Brand = brand,
                Category = category,
                Confidence = 0.67m
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Go-UPC lookup failed for code {Code}", normalizedCode);
            return null;
        }
    }

    private async Task<ExternalBarcodeLookupResult?> TryUpcItemDbByTitleAsync(
        string titleQuery,
        string? platformHint,
        CancellationToken cancellationToken)
    {
        if (!_options.UpcItemDb.Enabled)
        {
            return null;
        }

        var normalizedTitleQuery = NormalizeWhitespace(titleQuery);
        if (string.IsNullOrWhiteSpace(normalizedTitleQuery))
        {
            return null;
        }

        var baseUrl = _options.UpcItemDb.ApiBaseUrl.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return null;
        }

        var userKey = _options.UpcItemDb.UserKey.Trim();
        var useTrial = string.IsNullOrWhiteSpace(userKey) && _options.UpcItemDb.UseTrialWithoutKey;
        if (!useTrial && string.IsNullOrWhiteSpace(userKey))
        {
            return null;
        }

        var endpoint = useTrial
            ? $"{baseUrl}/prod/trial/search?s={Uri.EscapeDataString(normalizedTitleQuery)}"
            : $"{baseUrl}/prod/v1/search?s={Uri.EscapeDataString(normalizedTitleQuery)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        ApplyCommonHeaders(request);
        if (!useTrial)
        {
            request.Headers.TryAddWithoutValidation("user_key", userKey);
            request.Headers.TryAddWithoutValidation("key_type", _options.UpcItemDb.KeyType.Trim());
        }

        try
        {
            using var response = await SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            ExternalBarcodeLookupResult? best = null;
            foreach (var item in items.EnumerateArray())
            {
                var title = NormalizeWhitespace(GetString(item, "title"));
                if (string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                var category = NormalizeWhitespace(GetString(item, "category"));
                var brand = NormalizeWhitespace(GetString(item, "brand"));
                if (!LooksLikeVideoGame(title, category, platformHint))
                {
                    continue;
                }

                var upc = NormalizeCode(GetString(item, "upc"));
                var ean = NormalizeCode(GetString(item, "ean"));
                var code = IsSupportedRetailCode(upc)
                    ? upc
                    : IsSupportedRetailCode(ean)
                        ? ean
                        : string.Empty;
                if (string.IsNullOrWhiteSpace(code))
                {
                    continue;
                }

                var similarity = ScoreTitleSimilarity(normalizedTitleQuery, title);
                if (similarity < 0.58m)
                {
                    continue;
                }

                var platform = InferPlatform(title, category, platformHint);
                if (HasConflictingNumericTokens(normalizedTitleQuery, title))
                {
                    continue;
                }

                var confidence = decimal.Min(0.92m, decimal.Max(0.6m, 0.54m + (similarity * 0.34m)));
                var candidate = new ExternalBarcodeLookupResult
                {
                    Code = code,
                    Title = title,
                    Platform = platform,
                    Provider = "UPCitemdb search",
                    Brand = brand,
                    Category = category,
                    Confidence = confidence
                };

                if (best is null || candidate.Confidence > best.Confidence)
                {
                    best = candidate;
                }
            }

            return best;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "UPCitemdb title search failed for query {Query}", normalizedTitleQuery);
            return null;
        }
    }

    private async Task<ExternalBarcodeLookupResult?> TryEanDbAsync(
        string normalizedCode,
        string? platformHint,
        CancellationToken cancellationToken)
    {
        if (!_options.EanDb.Enabled)
        {
            return null;
        }

        var baseUrl = _options.EanDb.ApiBaseUrl.TrimEnd('/');
        var token = _options.EanDb.JwtToken.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var endpoint = $"{baseUrl}/product/{Uri.EscapeDataString(normalizedCode)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        ApplyCommonHeaders(request);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");

        try
        {
            using var response = await SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound || !response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("product", out var product))
            {
                return null;
            }

            var title = NormalizeWhitespace(GetLocalizedTitle(product, "titles"));
            if (string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            var category = NormalizeWhitespace(GetFirstLocalizedCategory(product));
            var brand = NormalizeWhitespace(GetManufacturerName(product));
            if (!LooksLikeVideoGame(title, category, platformHint))
            {
                return null;
            }

            return new ExternalBarcodeLookupResult
            {
                Code = normalizedCode,
                Title = title,
                Platform = InferPlatform(title, category, platformHint),
                Provider = "EAN-DB",
                Brand = brand,
                Category = category,
                Confidence = 0.72m
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "EAN-DB lookup failed for code {Code}", normalizedCode);
            return null;
        }
    }

    private async Task<ExternalBarcodeLookupResult> EnrichWithMobyAsync(
        ExternalBarcodeLookupResult candidate,
        string? platformHint,
        CancellationToken cancellationToken)
    {
        var moby = await TryMobyByTitleAsync(candidate.Title, platformHint, strictTitleScore: false, cancellationToken);
        if (moby is null)
        {
            return candidate;
        }

        return candidate with
        {
            Title = moby.Title,
            Platform = string.IsNullOrWhiteSpace(moby.Platform) ? candidate.Platform : moby.Platform,
            Franchise = string.IsNullOrWhiteSpace(moby.Franchise) ? candidate.Franchise : moby.Franchise,
            Provider = $"{candidate.Provider} -> MobyGames",
            Confidence = decimal.Min(0.95m, candidate.Confidence + 0.15m)
        };
    }

    private async Task<ExternalBarcodeLookupResult?> TryMobyByTitleAsync(
        string titleQuery,
        string? platformHint,
        bool strictTitleScore,
        CancellationToken cancellationToken)
    {
        if (!_options.MobyGames.Enabled)
        {
            return null;
        }

        var baseUrl = _options.MobyGames.ApiBaseUrl.TrimEnd('/');
        var apiKey = _options.MobyGames.ApiKey.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(titleQuery))
        {
            return null;
        }

        var maxResults = Math.Clamp(_options.MobyGames.MaxResults, 1, 12);
        var endpoint = $"{baseUrl}/games?api_key={Uri.EscapeDataString(apiKey)}&title={Uri.EscapeDataString(titleQuery)}&format=brief&limit={maxResults}";
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        ApplyCommonHeaders(request);

        try
        {
            using var response = await SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("games", out var games) || games.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            ExternalBarcodeLookupResult? best = null;
            foreach (var game in games.EnumerateArray())
            {
                var title = NormalizeWhitespace(GetString(game, "title"));
                if (string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                var similarity = ScoreTitleSimilarity(titleQuery, title);
                var minScore = strictTitleScore ? 0.88m : 0.56m;
                if (similarity < minScore)
                {
                    continue;
                }

                var platform = string.IsNullOrWhiteSpace(platformHint) ? string.Empty : platformHint.Trim();
                var candidate = new ExternalBarcodeLookupResult
                {
                    Title = title,
                    Platform = platform,
                    Provider = "MobyGames",
                    Confidence = decimal.Min(0.93m, 0.66m + (similarity * 0.25m))
                };

                if (best is null || candidate.Confidence > best.Confidence)
                {
                    best = candidate;
                }
            }

            return best;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MobyGames lookup failed for title query {Query}", titleQuery);
            return null;
        }
    }

    private async Task<ExternalBarcodeLookupResult?> CanonicalizeWithTitleBankAsync(
        ExternalBarcodeLookupResult lookup,
        string? platformHint,
        CancellationToken cancellationToken)
    {
        try
        {
            var match = await _titleBankService.FindBestMatchAsync(
                lookup.Title,
                string.IsNullOrWhiteSpace(lookup.Platform) ? platformHint : lookup.Platform,
                cancellationToken);
            if (match is null || match.Score < TitleBankCanonicalizationMinScore)
            {
                return lookup;
            }

            if (HasConflictingNumericTokens(lookup.Title, match.Entry.Title))
            {
                return lookup;
            }

            return lookup with
            {
                Title = match.Entry.Title,
                Platform = string.IsNullOrWhiteSpace(match.Entry.Platform) ? lookup.Platform : match.Entry.Platform,
                Franchise = string.IsNullOrWhiteSpace(lookup.Franchise) ? match.Entry.Franchise : lookup.Franchise,
                Confidence = decimal.Min(0.97m, lookup.Confidence + (match.Score * 0.1m))
            };
        }
        catch
        {
            return lookup;
        }
    }

    private void ApplyCommonHeaders(HttpRequestMessage request)
    {
        var userAgent = _options.UserAgent.Trim();
        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            request.Headers.UserAgent.Clear();
            request.Headers.UserAgent.ParseAdd(userAgent);
        }
    }

    private static ExternalBarcodeLookupResult MergeProviderInfo(
        ExternalBarcodeLookupResult primary,
        ExternalBarcodeLookupResult secondary)
    {
        var primaryProvider = (primary.Provider ?? string.Empty).Trim();
        var secondaryProvider = (secondary.Provider ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(secondaryProvider) ||
            primaryProvider.Contains(secondaryProvider, StringComparison.OrdinalIgnoreCase))
        {
            return primary;
        }

        var provider = string.IsNullOrWhiteSpace(primaryProvider)
            ? secondaryProvider
            : $"{primaryProvider} | {secondaryProvider}";
        return primary with { Provider = provider };
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var timeoutSeconds = Math.Clamp(_options.RequestTimeoutSeconds, 3, 45);
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        return await _httpClient.SendAsync(request, linked.Token);
    }

    private static string GetString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return string.Empty;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            _ => string.Empty
        };
    }

    private static string GetLocalizedTitle(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var titles) || titles.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        if (titles.TryGetProperty("en", out var en) && en.ValueKind == JsonValueKind.String)
        {
            return en.GetString() ?? string.Empty;
        }

        foreach (var propertyValue in titles.EnumerateObject())
        {
            if (propertyValue.Value.ValueKind == JsonValueKind.String)
            {
                return propertyValue.Value.GetString() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private static string GetFirstLocalizedCategory(JsonElement product)
    {
        if (!product.TryGetProperty("categories", out var categories) || categories.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        foreach (var category in categories.EnumerateArray())
        {
            var localized = GetLocalizedTitle(category, "titles");
            if (!string.IsNullOrWhiteSpace(localized))
            {
                return localized;
            }
        }

        return string.Empty;
    }

    private static string GetManufacturerName(JsonElement product)
    {
        if (!product.TryGetProperty("manufacturer", out var manufacturer) || manufacturer.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        var localized = GetLocalizedTitle(manufacturer, "titles");
        if (!string.IsNullOrWhiteSpace(localized))
        {
            return localized;
        }

        return GetString(manufacturer, "name");
    }

    private static bool LooksLikeVideoGame(string title, string category, string? platformHint)
    {
        var searchable = $"{title} {category}".ToLowerInvariant();
        if (NonGameAccessoryRegex().IsMatch(searchable))
        {
            return false;
        }

        if (searchable.Contains("console", StringComparison.Ordinal) &&
            !searchable.Contains("video game", StringComparison.Ordinal))
        {
            return false;
        }

        if (VideoGameKeywordRegex().IsMatch(searchable))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(platformHint))
        {
            return true;
        }

        // Accept unknown category/title when it at least looks like media software.
        return searchable.Contains("software", StringComparison.Ordinal);
    }

    private static string InferPlatform(string title, string category, string? platformHint)
    {
        if (!string.IsNullOrWhiteSpace(platformHint))
        {
            return platformHint.Trim();
        }

        var searchable = $"{title} {category}".ToLowerInvariant();
        if (searchable.Contains("playstation 5", StringComparison.Ordinal) || searchable.Contains("ps5", StringComparison.Ordinal))
        {
            return "PlayStation 5";
        }

        if (searchable.Contains("playstation 4", StringComparison.Ordinal) || searchable.Contains("ps4", StringComparison.Ordinal))
        {
            return "PlayStation 4";
        }

        if (searchable.Contains("playstation 3", StringComparison.Ordinal) || searchable.Contains("ps3", StringComparison.Ordinal))
        {
            return "PlayStation 3";
        }

        if (searchable.Contains("xbox 360", StringComparison.Ordinal))
        {
            return "Xbox 360";
        }

        if (searchable.Contains("xbox one", StringComparison.Ordinal))
        {
            return "Xbox One";
        }

        if (searchable.Contains("xbox series", StringComparison.Ordinal))
        {
            return "Xbox Series X";
        }

        if (searchable.Contains("nintendo switch", StringComparison.Ordinal))
        {
            return "Nintendo Switch";
        }

        if (searchable.Contains("wii", StringComparison.Ordinal))
        {
            return "Nintendo Wii";
        }

        if (searchable.Contains("gamecube", StringComparison.Ordinal))
        {
            return "Nintendo GameCube";
        }

        return string.Empty;
    }

    private static decimal ScoreTitleSimilarity(string source, string candidate)
    {
        var a = NormalizeText(source);
        var b = NormalizeText(candidate);
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return 0m;
        }

        if (a == b)
        {
            return 1m;
        }

        var aTokens = a.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var bTokens = b.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (aTokens.Length == 0 || bTokens.Length == 0)
        {
            return 0m;
        }

        var overlap = aTokens.Count(token => bTokens.Contains(token, StringComparer.Ordinal));
        var overlapScore = (decimal)overlap / decimal.Max(aTokens.Length, bTokens.Length);

        var containsScore = 0m;
        if (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal))
        {
            var shorter = Math.Min(aTokens.Length, bTokens.Length);
            var longer = Math.Max(aTokens.Length, bTokens.Length);
            var expansion = longer - shorter;
            containsScore = expansion switch
            {
                <= 1 => 0.9m,
                2 => 0.82m,
                3 => 0.76m,
                _ => 0.68m
            };
        }

        var score = decimal.Max(overlapScore, containsScore);
        if (HasConflictingNumericTokens(a, b))
        {
            score -= 0.22m;
        }

        score -= ScoreEditionPenalty(a, b);
        return decimal.Max(0m, decimal.Min(1m, score));
    }

    private static decimal ScoreUpcItemDbCandidate(
        string title,
        string category,
        string inferredPlatform,
        string normalizedPlatformHint,
        decimal titleBankScore)
    {
        var normalizedTitle = NormalizeText(title);
        var normalizedCategory = NormalizeText(category);
        var normalizedPlatform = NormalizeText(inferredPlatform);

        var score = 0.43m;
        score += decimal.Min(0.36m, titleBankScore * 0.36m);

        if (!string.IsNullOrWhiteSpace(normalizedCategory))
        {
            if (normalizedCategory.Contains("video game", StringComparison.Ordinal) ||
                normalizedCategory.Contains("games", StringComparison.Ordinal))
            {
                score += 0.1m;
            }
            else if (normalizedCategory.Contains("software", StringComparison.Ordinal))
            {
                score += 0.06m;
            }
        }

        if (!string.IsNullOrWhiteSpace(normalizedPlatformHint))
        {
            if (normalizedPlatform == normalizedPlatformHint)
            {
                score += 0.08m;
            }
            else if (!string.IsNullOrWhiteSpace(normalizedPlatform) &&
                     (normalizedPlatform.Contains(normalizedPlatformHint, StringComparison.Ordinal) ||
                      normalizedPlatformHint.Contains(normalizedPlatform, StringComparison.Ordinal)))
            {
                score += 0.04m;
            }
            else if (!string.IsNullOrWhiteSpace(normalizedPlatform))
            {
                score -= 0.15m;
            }
        }

        if (NonGameAccessoryRegex().IsMatch($"{normalizedTitle} {normalizedCategory}"))
        {
            score -= 0.35m;
        }

        score -= ScoreEditionPenalty(normalizedTitle);
        return decimal.Max(0m, decimal.Min(0.94m, score));
    }

    private static decimal ScoreEditionPenalty(string normalizedTitle)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle))
        {
            return 0m;
        }

        if (EditionHeavyTokenRegex().IsMatch(normalizedTitle))
        {
            return 0.08m;
        }

        if (AddOnTokenRegex().IsMatch(normalizedTitle))
        {
            return 0.14m;
        }

        return 0m;
    }

    private static decimal ScoreEditionPenalty(string normalizedSource, string normalizedCandidate)
    {
        if (string.IsNullOrWhiteSpace(normalizedSource) || string.IsNullOrWhiteSpace(normalizedCandidate))
        {
            return 0m;
        }

        var sourceHasEdition = EditionHeavyTokenRegex().IsMatch(normalizedSource);
        var candidateHasEdition = EditionHeavyTokenRegex().IsMatch(normalizedCandidate);
        if (candidateHasEdition && !sourceHasEdition)
        {
            return 0.1m;
        }

        var sourceHasAddon = AddOnTokenRegex().IsMatch(normalizedSource);
        var candidateHasAddon = AddOnTokenRegex().IsMatch(normalizedCandidate);
        if (candidateHasAddon && !sourceHasAddon)
        {
            return 0.16m;
        }

        return 0m;
    }

    private static bool HasConflictingNumericTokens(string left, string right)
    {
        var leftTokens = ExtractNumericTokens(left);
        var rightTokens = ExtractNumericTokens(right);
        if (leftTokens.Count == 0 || rightTokens.Count == 0)
        {
            return false;
        }

        return !leftTokens.Overlaps(rightTokens);
    }

    private static HashSet<string> ExtractNumericTokens(string value)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(value))
        {
            return tokens;
        }

        foreach (Match match in NumericTokenRegex().Matches(value))
        {
            var token = match.Value.Trim();
            if (!string.IsNullOrWhiteSpace(token))
            {
                tokens.Add(token);
            }
        }

        return tokens;
    }

    private static string NormalizeCode(string code)
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

    private static bool IsSupportedRetailCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code) || !code.All(char.IsDigit))
        {
            return false;
        }

        return code.Length switch
        {
            12 => IsUpcAChecksumValid(code),
            13 => IsEanChecksumValid(code),
            _ => false
        };
    }

    private static bool IsEanChecksumValid(string code)
    {
        if (code.Length != 13 || !code.All(char.IsDigit))
        {
            return false;
        }

        var expected = code[^1] - '0';
        var sum = 0;
        for (var i = 0; i < 12; i++)
        {
            var digit = code[i] - '0';
            sum += i % 2 == 0 ? digit : digit * 3;
        }

        return (10 - (sum % 10)) % 10 == expected;
    }

    private static bool IsUpcAChecksumValid(string code)
    {
        if (code.Length != 12 || !code.All(char.IsDigit))
        {
            return false;
        }

        var expected = code[^1] - '0';
        var oddSum = 0;
        var evenSum = 0;
        for (var i = 0; i < 11; i++)
        {
            var digit = code[i] - '0';
            if (i % 2 == 0)
            {
                oddSum += digit;
            }
            else
            {
                evenSum += digit;
            }
        }

        var check = (10 - (((oddSum * 3) + evenSum) % 10)) % 10;
        return check == expected;
    }

    private static string NormalizeWhitespace(string value)
    {
        return WhiteSpaceRegex().Replace(value ?? string.Empty, " ").Trim();
    }

    private static string NormalizeText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var cleaned = NonAlphaNumericRegex().Replace(text.ToLowerInvariant(), " ");
        return NormalizeWhitespace(cleaned);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpaceRegex();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlphaNumericRegex();

    [GeneratedRegex(@"(playstation|ps[345]|xbox|nintendo|switch|wii|gamecube|video game|videogame|esrb|pegi)")]
    private static partial Regex VideoGameKeywordRegex();

    [GeneratedRegex(@"\b(controller|headset|charger|console|skin|cable|stand|case|memory card|guide|strategy guide)\b")]
    private static partial Regex NonGameAccessoryRegex();

    [GeneratedRegex(@"\b(game of the year|goty|collector(?:'s)?|limited|definitive|ultimate|complete|special edition|platinum|greatest hits)\b")]
    private static partial Regex EditionHeavyTokenRegex();

[GeneratedRegex(@"\b(multiplayer pack|pack|dlc|expansion|season pass|add on|addon)\b")]
private static partial Regex AddOnTokenRegex();

    [GeneratedRegex(@"\d+")]
    private static partial Regex NumericTokenRegex();

    private sealed class CachedLookupResult
    {
        public ExternalBarcodeLookupResult? Result { get; init; }
    }
}
