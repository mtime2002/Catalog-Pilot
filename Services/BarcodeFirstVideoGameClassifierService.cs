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
        if (!CanRecoverBarcodeFromTitle(ocrResult))
        {
            return null;
        }

        var platformHint = ResolvePlatformHint(input, ocrResult);
        var lookup = await _externalBarcodeLookupService.LookupByTitleAsync(
            ocrResult.SuggestedTitle,
            platformHint,
            cancellationToken);
        if (lookup is null || string.IsNullOrWhiteSpace(lookup.Code))
        {
            return null;
        }

        return BuildRecoveredBarcodeClassification(ocrResult, lookup);
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

    private static bool CanRecoverBarcodeFromTitle(ClassificationResult ocrResult)
    {
        if (string.IsNullOrWhiteSpace(ocrResult.SuggestedTitle))
        {
            return false;
        }

        if (LooksLikeNoisyTitle(ocrResult.SuggestedTitle))
        {
            return false;
        }

        return ocrResult.BankMatchScore >= 0.52m || ocrResult.Confidence >= 0.75m;
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
        ExternalBarcodeLookupResult lookup)
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
            SuggestedTitle = string.IsNullOrWhiteSpace(lookup.Title) ? ocrResult.SuggestedTitle : lookup.Title,
            SuggestedPlatform = platform,
            BankMatchedTitle = string.IsNullOrWhiteSpace(lookup.Title) ? ocrResult.BankMatchedTitle : lookup.Title,
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
}
