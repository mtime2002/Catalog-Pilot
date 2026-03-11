using CatalogPilot.Models;

namespace CatalogPilot.Services;

public sealed class BarcodeFirstVideoGameClassifierService : IVideoGameClassifierService
{
    private readonly IBarcodeGameClassifierService _barcodeClassifier;
    private readonly LocalOcrVideoGameClassifierService _ocrClassifier;
    private readonly IExternalBarcodeLookupService _externalBarcodeLookupService;
    private readonly VisionAssistedVideoGameClassifierService _visionClassifier;

    public BarcodeFirstVideoGameClassifierService(
        IBarcodeGameClassifierService barcodeClassifier,
        LocalOcrVideoGameClassifierService ocrClassifier,
        IExternalBarcodeLookupService externalBarcodeLookupService,
        VisionAssistedVideoGameClassifierService visionClassifier)
    {
        _barcodeClassifier = barcodeClassifier;
        _ocrClassifier = ocrClassifier;
        _externalBarcodeLookupService = externalBarcodeLookupService;
        _visionClassifier = visionClassifier;
    }

    public async Task<ClassificationResult> ClassifyAsync(ListingInput input, CancellationToken cancellationToken = default)
    {
        var barcodeResult = await _barcodeClassifier.TryClassifyAsync(input, cancellationToken);
        if (barcodeResult is not null)
        {
            return barcodeResult;
        }

        var ocrResult = await _ocrClassifier.ClassifyAsync(input, cancellationToken);
        var recoveredByTitle = await TryRecoverBarcodeFromTitleAsync(input, ocrResult, cancellationToken);
        if (recoveredByTitle is not null)
        {
            return recoveredByTitle;
        }

        if (!ShouldTryVisionFallback(ocrResult))
        {
            return ocrResult;
        }

        var visionResult = await _visionClassifier.ClassifyAsync(input, cancellationToken);
        var bestFallback = IsUsableVisionResult(visionResult) ? visionResult : ocrResult;
        var recoveredFromBestFallback = await TryRecoverBarcodeFromTitleAsync(input, bestFallback, cancellationToken);
        return recoveredFromBestFallback ?? bestFallback;
    }

    private async Task<ClassificationResult?> TryRecoverBarcodeFromTitleAsync(
        ListingInput input,
        ClassificationResult ocrResult,
        CancellationToken cancellationToken)
    {
        if (!TryResolveRecoveryTitle(ocrResult, out var recoveryTitle))
        {
            return null;
        }

        var platformHint = ResolvePlatformHint(input, ocrResult);
        var lookup = await _externalBarcodeLookupService.LookupByTitleAsync(
            recoveryTitle,
            platformHint,
            cancellationToken);
        if (lookup is null || string.IsNullOrWhiteSpace(lookup.Code))
        {
            return null;
        }

        if (ScoreTitleOverlap(recoveryTitle, lookup.Title) < 0.34m)
        {
            return null;
        }

        return BuildRecoveredBarcodeClassification(ocrResult, lookup, recoveryTitle);
    }

    private static bool ShouldTryVisionFallback(ClassificationResult ocrResult)
    {
        if (ocrResult.Source.Contains("low confidence", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (LooksLikeNoisyTitle(ocrResult.SuggestedTitle))
        {
            return true;
        }

        return ocrResult.BankMatchScore < 0.5m && ocrResult.Confidence < 0.72m;
    }

    private static bool IsUsableVisionResult(ClassificationResult visionResult)
    {
        if (!visionResult.Source.Contains("Vision", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(visionResult.SuggestedTitle))
        {
            return false;
        }

        if (visionResult.SuggestedTitle.Equals("Video Game", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !LooksLikeNoisyTitle(visionResult.SuggestedTitle);
    }

    private static bool LooksLikeNoisyTitle(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Contains("playstation-network", StringComparison.Ordinal) ||
            normalized.Contains("only on playstation", StringComparison.Ordinal) ||
            normalized.Contains("online interactions", StringComparison.Ordinal) ||
            normalized.Contains("not rated", StringComparison.Ordinal) ||
            normalized.Contains("raned by", StringComparison.Ordinal))
        {
            return true;
        }

        var tokens = normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length < 7)
        {
            return false;
        }

        var shortOrNumeric = tokens.Count(token => token.Length <= 3 || token.All(char.IsDigit));
        return shortOrNumeric / (double)tokens.Length >= 0.55;
    }

    private static bool TryResolveRecoveryTitle(ClassificationResult ocrResult, out string title)
    {
        title = string.Empty;

        if (!string.IsNullOrWhiteSpace(ocrResult.SuggestedTitle) &&
            !LooksLikeNoisyTitle(ocrResult.SuggestedTitle))
        {
            var cleaned = SanitizeRecoveryTitle(ocrResult.SuggestedTitle);
            if (!string.IsNullOrWhiteSpace(cleaned))
            {
                title = cleaned;
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(ocrResult.BankMatchedTitle) &&
            !LooksLikeNoisyTitle(ocrResult.BankMatchedTitle) &&
            ocrResult.BankMatchScore >= 0.24m)
        {
            var cleaned = SanitizeRecoveryTitle(ocrResult.BankMatchedTitle);
            if (!string.IsNullOrWhiteSpace(cleaned))
            {
                title = cleaned;
                return true;
            }
        }

        if (TryGetItemSpecificValue(ocrResult, "OCR Candidate Title", out var ocrCandidate) &&
            !LooksLikeNoisyTitle(ocrCandidate) &&
            (ocrResult.Confidence >= 0.72m || ocrResult.BankMatchScore >= 0.24m))
        {
            var cleaned = SanitizeRecoveryTitle(ocrCandidate);
            if (!string.IsNullOrWhiteSpace(cleaned))
            {
                title = cleaned;
                return true;
            }
        }

        if (TryGetItemSpecificValue(ocrResult, "Bank Candidate Title", out var bankCandidate) &&
            !LooksLikeNoisyTitle(bankCandidate) &&
            ocrResult.BankMatchScore >= 0.2m)
        {
            var cleaned = SanitizeRecoveryTitle(bankCandidate);
            if (!string.IsNullOrWhiteSpace(cleaned))
            {
                title = cleaned;
                return true;
            }
        }

        return false;
    }

    private static string ResolvePlatformHint(ListingInput input, ClassificationResult ocrResult)
    {
        if (!string.IsNullOrWhiteSpace(input.Platform))
        {
            return input.Platform;
        }

        if (!string.IsNullOrWhiteSpace(ocrResult.BankMatchedPlatform))
        {
            return ocrResult.BankMatchedPlatform;
        }

        return ocrResult.SuggestedPlatform;
    }

    private static ClassificationResult BuildRecoveredBarcodeClassification(
        ClassificationResult ocrResult,
        ExternalBarcodeLookupResult lookup,
        string recoveryTitle)
    {
        var platform = string.IsNullOrWhiteSpace(lookup.Platform)
            ? ocrResult.SuggestedPlatform
            : lookup.Platform;

        var specifics = new Dictionary<string, string>(ocrResult.ItemSpecifics, StringComparer.OrdinalIgnoreCase);
        specifics["Detected Game Code"] = lookup.Code;
        specifics["Barcode Provider"] = lookup.Provider;
        if (!string.IsNullOrWhiteSpace(platform))
        {
            specifics["Platform"] = platform;
        }

        return new ClassificationResult
        {
            Source = "OCR + External Title->Barcode Recovery",
            SuggestedTitle = string.IsNullOrWhiteSpace(lookup.Title) ? recoveryTitle : lookup.Title,
            SuggestedPlatform = platform,
            BankMatchedTitle = string.IsNullOrWhiteSpace(lookup.Title) ? recoveryTitle : lookup.Title,
            BankMatchedPlatform = platform,
            BankMatchScore = decimal.Max(ocrResult.BankMatchScore, lookup.Confidence),
            SuggestedCondition = ocrResult.SuggestedCondition,
            Franchise = string.IsNullOrWhiteSpace(lookup.Franchise) ? ocrResult.Franchise : lookup.Franchise,
            Edition = ocrResult.Edition,
            CategoryId = ocrResult.CategoryId,
            Confidence = decimal.Min(0.97m, decimal.Max(ocrResult.Confidence, lookup.Confidence + 0.12m)),
            ItemSpecifics = specifics
        };
    }

    private static bool TryGetItemSpecificValue(ClassificationResult result, string key, out string value)
    {
        value = string.Empty;
        if (result.ItemSpecifics is null || !result.ItemSpecifics.TryGetValue(key, out var found))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(found))
        {
            return false;
        }

        value = found.Trim();
        return true;
    }

    private static decimal ScoreTitleOverlap(string source, string candidate)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(candidate))
        {
            return 0m;
        }

        static string[] Tokens(string value) => value
            .ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => new string(token.Where(char.IsLetterOrDigit).ToArray()))
            .Where(token => token.Length >= 2)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var a = Tokens(source);
        var b = Tokens(candidate);
        if (a.Length == 0 || b.Length == 0)
        {
            return 0m;
        }

        var overlap = a.Count(token => b.Contains(token, StringComparer.Ordinal));
        return (decimal)overlap / decimal.Max(a.Length, b.Length);
    }

    private static string SanitizeRecoveryTitle(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var lowered = value
            .Replace(':', ' ')
            .Replace('-', ' ')
            .Replace('.', ' ')
            .ToLowerInvariant();

        var stopPhrases = new[]
        {
            " is a trademark",
            " trademark of",
            " created and developed",
            " sony computer",
            " entertainment america",
            " www "
        };

        var cutoff = lowered.Length;
        foreach (var phrase in stopPhrases)
        {
            var index = lowered.IndexOf(phrase, StringComparison.Ordinal);
            if (index >= 0 && index < cutoff)
            {
                cutoff = index;
            }
        }

        var window = cutoff < lowered.Length ? lowered[..cutoff] : lowered;
        var tokens = window
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Any(char.IsLetterOrDigit))
            .Select(token => new string(token.Where(char.IsLetterOrDigit).ToArray()))
            .Where(token => token.Length >= 2)
            .Take(8)
            .ToArray();

        if (tokens.Length == 0)
        {
            return string.Empty;
        }

        return string.Join(' ', tokens);
    }
}
