using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CatalogPilot.Models;
using CatalogPilot.Options;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace CatalogPilot.Services;

public sealed partial class LocalOcrVideoGameClassifierService : IVideoGameClassifierService
{
    private static readonly Dictionary<string, string[]> PlatformKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Nintendo Switch"] = ["switch", "nintendo switch"],
        ["PlayStation 5"] = ["ps5", "playstation 5"],
        ["PlayStation 4"] = ["ps4", "playstation 4"],
        ["PlayStation 3"] = ["ps3", "playstation 3"],
        ["Xbox Series X"] = ["xbox series", "series x"],
        ["Xbox One"] = ["xbox one"],
        ["Xbox 360"] = ["xbox 360"],
        ["Nintendo Wii"] = ["wii"],
        ["Nintendo GameCube"] = ["gamecube", "game cube"],
        ["Nintendo 64"] = ["n64", "nintendo 64"],
        ["SNES"] = ["snes", "super nintendo"],
        ["NES"] = ["nes", "nintendo entertainment system"]
    };

    private static readonly string[] BlockedTitleTerms =
    [
        "nintendo",
        "playstation",
        "playstation network",
        "station network",
        "network entertainment",
        "xbox",
        "official",
        "seal",
        "esrb",
        "rating",
        "teen",
        "mature",
        "everyone",
        "licensed",
        "manual",
        "warranty",
        "instruction",
        "not rated",
        "raned",
        "online interactions",
        "only on",
        "esr",
        "pegi"
    ];

    private static readonly HashSet<string> SeedNoiseTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "db",
        "dvd",
        "disc",
        "case",
        "cover",
        "box",
        "video",
        "game",
        "network",
        "entertainment",
        "psn"
    };

    private static readonly HashSet<string> TitleNoiseTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "official",
        "rating",
        "teen",
        "mature",
        "everyone",
        "stars",
        "star",
        "etoiles",
        "sur",
        "supplement",
        "only",
        "magazine",
        "not",
        "rated",
        "raned",
        "esr",
        "online",
        "network",
        "entertainment",
        "psn",
        "ment",
        "interactions",
        "pegi",
        "create",
        "share",
        "missions",
        "the",
        "and",
        "for",
        "with",
        "from",
        "only",
        "on",
        "by",
        "to",
        "a",
        "an"
    };

    private readonly LocalClassifierOptions _options;
    private readonly IWebHostEnvironment _hostEnvironment;
    private readonly RuleBasedVideoGameClassifierService _fallbackClassifier;
    private readonly IGameTitleBankService _titleBankService;
    private readonly ILogger<LocalOcrVideoGameClassifierService> _logger;
    private readonly object _runtimeLock = new();
    private string _resolvedPythonExecutable = string.Empty;
    private string _resolvedScriptPath = string.Empty;
    private bool _runtimeLookupAttempted;
    private bool _missingRuntimeLogged;

    public LocalOcrVideoGameClassifierService(
        IOptions<LocalClassifierOptions> options,
        IWebHostEnvironment hostEnvironment,
        RuleBasedVideoGameClassifierService fallbackClassifier,
        IGameTitleBankService titleBankService,
        ILogger<LocalOcrVideoGameClassifierService> logger)
    {
        _options = options.Value;
        _hostEnvironment = hostEnvironment;
        _fallbackClassifier = fallbackClassifier;
        _titleBankService = titleBankService;
        _logger = logger;
    }

    public async Task<ClassificationResult> ClassifyAsync(ListingInput input, CancellationToken cancellationToken = default)
    {
        var fallback = await _fallbackClassifier.ClassifyAsync(input, cancellationToken);
        if (!_options.Enabled || input.Photos.Count == 0)
        {
            return fallback;
        }

        var ocrSignals = await ExtractOcrSignalsAsync(input, cancellationToken);
        var ocrText = string.Join(
            Environment.NewLine,
            ocrSignals
                .Select(signal => signal.Text)
                .Where(text => !string.IsNullOrWhiteSpace(text)));
        if (string.IsNullOrWhiteSpace(ocrText))
        {
            return new ClassificationResult
            {
                Source = "PaddleOCR unavailable + Rule-based",
                SuggestedTitle = string.Empty,
                SuggestedPlatform = fallback.SuggestedPlatform,
                BankMatchedTitle = string.Empty,
                BankMatchedPlatform = string.Empty,
                BankMatchScore = 0m,
                SuggestedCondition = fallback.SuggestedCondition,
                Franchise = fallback.Franchise,
                Edition = fallback.Edition,
                CategoryId = fallback.CategoryId,
                Confidence = fallback.Confidence,
                ItemSpecifics = new Dictionary<string, string>(fallback.ItemSpecifics, StringComparer.OrdinalIgnoreCase)
            };
        }

        var titleFromOcr = InferTitleFromOcr(ocrText, string.Empty, string.Empty);
        var platformFromOcr = InferPlatformFromOcr(ocrText, fallback.SuggestedPlatform, input.Platform);
        var bankSelection = await ResolveBestBankMatchAsync(
            ocrSignals,
            ocrText,
            titleFromOcr,
            platformFromOcr,
            cancellationToken);
        var bankMatch = bankSelection?.Match;
        var bankMatchedTitle = bankMatch?.Entry.Title ?? string.Empty;
        var bankMatchedPlatform = bankMatch?.Entry.Platform ?? string.Empty;
        var bankMatchScore = bankMatch?.Score ?? 0m;
        var bankConfidence = bankSelection?.Confidence ?? 0m;
        var bankThreshold = !string.IsNullOrWhiteSpace(platformFromOcr) ? 0.22m : 0.30m;
        var useBankTitle = bankSelection is not null &&
                           bankConfidence >= bankThreshold &&
                           !string.IsNullOrWhiteSpace(bankMatchedTitle);
        var title = useBankTitle ? bankMatchedTitle : string.Empty;
        var platform = useBankTitle && !string.IsNullOrWhiteSpace(bankMatchedPlatform)
            ? bankMatchedPlatform
            : platformFromOcr;
        var condition = InferConditionFromOcr(ocrText, fallback.SuggestedCondition);

        var specifics = new Dictionary<string, string>(fallback.ItemSpecifics, StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(platform))
        {
            specifics["Platform"] = platform;
        }
        if (!string.IsNullOrWhiteSpace(titleFromOcr) && !LooksLikeNoisyListingText(titleFromOcr))
        {
            specifics["OCR Candidate Title"] = titleFromOcr;
        }
        if (!string.IsNullOrWhiteSpace(bankMatchedTitle))
        {
            specifics["Bank Candidate Title"] = bankMatchedTitle;
        }
        if (bankSelection is not null && !string.IsNullOrWhiteSpace(bankSelection.Seed))
        {
            specifics["OCR Bank Seed"] = bankSelection.Seed;
            specifics["OCR Bank Confidence"] = bankSelection.Confidence.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
        }

        var confidenceBoost = 0m;
        if (useBankTitle)
        {
            confidenceBoost += 0.14m;
        }

        if (!string.IsNullOrWhiteSpace(platform) && !platform.Equals(fallback.SuggestedPlatform, StringComparison.OrdinalIgnoreCase))
        {
            confidenceBoost += 0.1m;
        }

        var source = useBankTitle
            ? "PaddleOCR + Per-image Catalog Match + Rule-based"
            : "PaddleOCR + Per-image Catalog Match (low confidence title match) + Rule-based";
        return new ClassificationResult
        {
            Source = source,
            SuggestedTitle = title,
            SuggestedPlatform = string.IsNullOrWhiteSpace(platform) ? fallback.SuggestedPlatform : platform,
            BankMatchedTitle = bankMatchedTitle,
            BankMatchedPlatform = bankMatchedPlatform,
            BankMatchScore = bankMatchScore,
            SuggestedCondition = condition,
            Franchise = fallback.Franchise,
            Edition = fallback.Edition,
            CategoryId = fallback.CategoryId,
            Confidence = decimal.Min(0.97m, fallback.Confidence + confidenceBoost),
            ItemSpecifics = specifics
        };
    }

    private async Task<List<OcrTextSignal>> ExtractOcrSignalsAsync(ListingInput input, CancellationToken cancellationToken)
    {
        var maxImages = Math.Clamp(Math.Min(_options.MaxImages, 2), 1, 3);
        var bundles = new List<ImageVariantBundle>(maxImages);
        var allPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selectedPhotos = SelectPhotosForOcr(input.Photos, maxImages);

        foreach (var photo in selectedPhotos)
        {
            var absolutePath = ResolvePhotoAbsolutePath(photo.RelativeUrl);
            if (string.IsNullOrWhiteSpace(absolutePath) || !File.Exists(absolutePath))
            {
                continue;
            }

            var bundle = BuildVariantBundle(absolutePath);
            bundles.Add(bundle);
            foreach (var path in bundle.AllPaths)
            {
                allPaths.Add(path);
            }
        }

        if (bundles.Count == 0 || allPaths.Count == 0)
        {
            return [];
        }

        var ocrByPath = await RunPaddleOcrBatchAsync(allPaths.ToArray(), cancellationToken);
        var signals = new List<OcrTextSignal>(bundles.Count);

        foreach (var bundle in bundles)
        {
            var merged = MergeSignalsForBundle(bundle, ocrByPath);
            if (!string.IsNullOrWhiteSpace(merged))
            {
                var ocrQuality = ScoreOcrText(merged);
                signals.Add(new OcrTextSignal(bundle.OriginalImagePath, merged, ocrQuality));
            }

            CleanupTemporaryVariants(bundle.OriginalImagePath, bundle.Variants);
            CleanupTempFile(bundle.TitleVariantPath);
            CleanupTempFile(bundle.PlatformVariantPath);
        }

        return signals;
    }

    private static IReadOnlyList<UploadedPhoto> SelectPhotosForOcr(IReadOnlyList<UploadedPhoto> photos, int maxImages)
    {
        if (photos.Count <= maxImages)
        {
            return photos;
        }

        if (maxImages <= 1)
        {
            return [photos[0]];
        }

        if (maxImages == 2)
        {
            return [photos[0], photos[^1]];
        }

        return photos.Take(maxImages).ToArray();
    }

    private ImageVariantBundle BuildVariantBundle(string imagePath)
    {
        var variants = CreateImageVariants(imagePath);
        var (titleVariant, platformVariant) = CreateFocusedSignalVariants(imagePath);
        var allPaths = variants
            .Concat([titleVariant, platformVariant])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new ImageVariantBundle(
            imagePath,
            variants,
            titleVariant,
            platformVariant,
            allPaths);
    }

    private static string MergeSignalsForBundle(
        ImageVariantBundle bundle,
        IReadOnlyDictionary<string, string> ocrByPath)
    {
        var bestText = string.Empty;
        var bestScore = decimal.MinValue;

        foreach (var variant in bundle.Variants)
        {
            if (!ocrByPath.TryGetValue(variant, out var text) || string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var score = ScoreOcrText(text);
            if (score <= bestScore)
            {
                continue;
            }

            bestScore = score;
            bestText = text;
        }

        ocrByPath.TryGetValue(bundle.TitleVariantPath, out var titleSignal);
        ocrByPath.TryGetValue(bundle.PlatformVariantPath, out var platformSignal);

        return string.Join(
            ' ',
            new[]
            {
                NormalizeWhitespace(titleSignal ?? string.Empty),
                NormalizeWhitespace(platformSignal ?? string.Empty),
                NormalizeWhitespace(bestText)
            }.Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    private async Task<Dictionary<string, string>> RunPaddleOcrBatchAsync(IReadOnlyList<string> imagePaths, CancellationToken cancellationToken)
    {
        var results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (imagePaths.Count == 0)
        {
            return results;
        }

        var (pythonExe, scriptPath) = ResolvePaddleRuntime();
        if (string.IsNullOrWhiteSpace(pythonExe) || string.IsNullOrWhiteSpace(scriptPath))
        {
            if (!_missingRuntimeLogged)
            {
                _logger.LogWarning(
                    "PaddleOCR runtime unavailable. Install Python + paddleocr + paddlepaddle and configure LocalClassifier:PythonExecutablePath / PaddleScriptPath.");
                _missingRuntimeLogged = true;
            }

            return results;
        }

        var timeoutSeconds = Math.Clamp(Math.Max(_options.ProcessTimeoutSeconds, 120), 20, 240);
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var args = new StringBuilder();
        args.Append('"').Append(scriptPath).Append('"');
        foreach (var imagePath in imagePaths)
        {
            args.Append(" --image \"").Append(imagePath).Append('"');
        }

        var outputFilePath = Path.Combine(Path.GetTempPath(), $"paddle-ocr-{Guid.NewGuid():N}.json");
        args.Append(" --out \"").Append(outputFilePath).Append('"');

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = pythonExe,
                    Arguments = args.ToString(),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(linkedCts.Token);
            var stdout = await outputTask;
            var stderr = await errorTask;

            var fileOutput = ReadOutputFile(outputFilePath);
            var parsed = ParsePaddleOutput(fileOutput);
            if (parsed.Count == 0)
            {
                parsed = ParsePaddleOutput(stdout);
            }
            if (parsed.Count == 0 && !string.IsNullOrWhiteSpace(stderr))
            {
                parsed = ParsePaddleOutput(stderr);
            }

            if (parsed.Count == 0 && (!string.IsNullOrWhiteSpace(stdout) || !string.IsNullOrWhiteSpace(stderr)))
            {
                parsed = ParsePaddleOutput($"{stdout}{Environment.NewLine}{stderr}");
            }

            if (process.ExitCode != 0)
            {
                _logger.LogWarning(
                    "PaddleOCR script exited with code {ExitCode}. stderr={Error}",
                    process.ExitCode,
                    Truncate(stderr, 320));
                return parsed;
            }

            if (parsed.Count == 0)
            {
                _logger.LogWarning(
                    "PaddleOCR returned no parseable text. file={FileOutput}; stdout={Stdout}; stderr={Stderr}",
                    Truncate(fileOutput, 320),
                    Truncate(stdout, 320),
                    Truncate(stderr, 320));
            }

            return parsed;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("PaddleOCR process timed out after {TimeoutSeconds}s", timeoutSeconds);
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PaddleOCR invocation failed");
            return results;
        }
        finally
        {
            CleanupTempFile(outputFilePath);
        }
    }

    private static string ReadOutputFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return string.Empty;
        }

        try
        {
            return File.ReadAllText(filePath, Encoding.UTF8);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static Dictionary<string, string> ParsePaddleOutput(string stdout)
    {
        var results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return results;
        }

        if (!TryParsePaddleJsonDocument(stdout, out var document) || document is null)
        {
            return results;
        }

        try
        {
            using var doc = document;
            var root = doc.RootElement;
            if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
            {
                return results;
            }

            if (!root.TryGetProperty("results", out var rows) || rows.ValueKind != JsonValueKind.Array)
            {
                return results;
            }

            foreach (var row in rows.EnumerateArray())
            {
                if (!row.TryGetProperty("image", out var imageElement))
                {
                    continue;
                }

                var image = imageElement.GetString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(image))
                {
                    continue;
                }

                var text = row.TryGetProperty("text", out var textElement)
                    ? textElement.GetString() ?? string.Empty
                    : string.Empty;
                results[image] = SanitizeOcrText(text);
            }
        }
        catch
        {
            // Ignore malformed OCR output and fallback to rule-based classification.
        }

        return results;
    }

    private static bool TryParsePaddleJsonDocument(string stdout, out JsonDocument? document)
    {
        document = null;

        if (TryParseJson(stdout, out document))
        {
            return true;
        }

        var lines = stdout
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith('{') && line.EndsWith('}'));

        foreach (var line in lines)
        {
            if (TryParseJson(line, out document))
            {
                return true;
            }
        }

        var start = stdout.IndexOf('{');
        var end = stdout.LastIndexOf('}');
        while (start >= 0 && end > start)
        {
            var candidate = stdout[start..(end + 1)];
            if (TryParseJson(candidate, out document))
            {
                return true;
            }

            start = stdout.IndexOf('{', start + 1);
        }

        return false;
    }

    private static bool TryParseJson(string rawJson, out JsonDocument? document)
    {
        document = null;
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return false;
        }

        try
        {
            document = JsonDocument.Parse(rawJson);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private (string pythonExecutable, string scriptPath) ResolvePaddleRuntime()
    {
        if (_runtimeLookupAttempted)
        {
            return (_resolvedPythonExecutable, _resolvedScriptPath);
        }

        lock (_runtimeLock)
        {
            if (_runtimeLookupAttempted)
            {
                return (_resolvedPythonExecutable, _resolvedScriptPath);
            }

            _resolvedPythonExecutable = ResolvePythonExecutable();
            _resolvedScriptPath = ResolveScriptPath();
            _runtimeLookupAttempted = true;
            return (_resolvedPythonExecutable, _resolvedScriptPath);
        }
    }

    private string ResolvePythonExecutable()
    {
        var configured = Environment.ExpandEnvironmentVariables((_options.PythonExecutablePath ?? string.Empty).Trim());
        if (string.IsNullOrWhiteSpace(configured))
        {
            return string.Empty;
        }

        if (configured.Contains(Path.DirectorySeparatorChar) || configured.Contains(Path.AltDirectorySeparatorChar))
        {
            return File.Exists(configured) ? configured : string.Empty;
        }

        return configured;
    }

    private string ResolveScriptPath()
    {
        var configured = Environment.ExpandEnvironmentVariables((_options.PaddleScriptPath ?? string.Empty).Trim());
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (Path.IsPathRooted(configured))
            {
                candidates.Add(configured);
            }
            else
            {
                candidates.Add(Path.Combine(_hostEnvironment.ContentRootPath, configured));
            }
        }

        candidates.Add(Path.Combine(_hostEnvironment.ContentRootPath, "scripts", "paddle_ocr.py"));

        return candidates.FirstOrDefault(File.Exists) ?? string.Empty;
    }

    private IReadOnlyList<string> CreateImageVariants(string originalImagePath)
    {
        var variants = new List<string> { originalImagePath };

        try
        {
            using var image = Image.Load<Rgba32>(originalImagePath);
            var maxWidth = 2200;
            var scale = image.Width < 1400 ? 2f : 1.5f;
            var targetWidth = Math.Min(maxWidth, (int)(image.Width * scale));
            var targetHeight = Math.Min(2200, (int)(image.Height * scale));

            var variantAPath = SaveVariant(image, targetWidth, targetHeight, 1.4f, 0.56f, 1.15f);
            if (!string.IsNullOrWhiteSpace(variantAPath))
            {
                variants.Add(variantAPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to generate OCR preprocessed variants for {ImagePath}", originalImagePath);
        }

        return variants;
    }

    private (string titleVariantPath, string platformVariantPath) CreateFocusedSignalVariants(string imagePath)
    {
        try
        {
            using var image = Image.Load<Rgba32>(imagePath);
            var titleRect = new Rectangle(
                (int)(image.Width * 0.05),
                (int)(image.Height * 0.15),
                Math.Max(20, (int)(image.Width * 0.90)),
                Math.Max(20, (int)(image.Height * 0.23)));

            var platformRect = new Rectangle(
                0,
                0,
                image.Width,
                Math.Max(20, (int)(image.Height * 0.14)));

            var title = SaveFocusedVariant(image, titleRect, 2.1f, 0.56f, 1.45f);
            var platform = SaveFocusedVariant(image, platformRect, 1.85f, 0.58f, 1.2f);
            return (title, platform);
        }
        catch
        {
            return (string.Empty, string.Empty);
        }
    }

    private static string SaveVariant(Image<Rgba32> source, int width, int height, float contrast, float threshold, float sharpen)
    {
        try
        {
            using var clone = source.Clone(ctx =>
            {
                ctx.Resize(new ResizeOptions
                {
                    Mode = ResizeMode.Max,
                    Size = new Size(width, height),
                    Sampler = KnownResamplers.Lanczos3
                });
                ctx.Grayscale();
                ctx.Contrast(contrast);
                ctx.GaussianSharpen(sharpen);
                ctx.BinaryThreshold(threshold);
            });

            var tempPath = Path.Combine(Path.GetTempPath(), $"ocr-{Guid.NewGuid():N}.png");
            clone.Save(tempPath, new PngEncoder { ColorType = PngColorType.Grayscale });
            return tempPath;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string SaveFocusedVariant(Image<Rgba32> source, Rectangle cropRect, float contrast, float threshold, float sharpen)
    {
        try
        {
            var imageBounds = new Rectangle(0, 0, source.Width, source.Height);
            var boundedRect = Rectangle.Intersect(imageBounds, cropRect);
            if (boundedRect.Width <= 1 || boundedRect.Height <= 1)
            {
                return string.Empty;
            }

            using var clone = source.Clone(ctx =>
            {
                ctx.Crop(boundedRect);
                var scale = boundedRect.Width < 1200 ? 2.1f : 1.3f;
                ctx.Resize(new ResizeOptions
                {
                    Mode = ResizeMode.Max,
                    Size = new Size(
                        Math.Max(100, (int)(boundedRect.Width * scale)),
                        Math.Max(60, (int)(boundedRect.Height * scale))),
                    Sampler = KnownResamplers.Lanczos3
                });
                ctx.Grayscale();
                ctx.Contrast(contrast);
                ctx.GaussianSharpen(sharpen);
                ctx.BinaryThreshold(threshold);
            });

            var tempPath = Path.Combine(Path.GetTempPath(), $"ocr-focus-{Guid.NewGuid():N}.png");
            clone.Save(tempPath, new PngEncoder { ColorType = PngColorType.Grayscale });
            return tempPath;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void CleanupTemporaryVariants(string originalImagePath, IReadOnlyList<string> variants)
    {
        foreach (var variant in variants)
        {
            if (variant.Equals(originalImagePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            CleanupTempFile(variant);
        }
    }

    private static void CleanupTempFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static decimal ScoreOcrText(string rawText)
    {
        var text = SanitizeOcrText(rawText);
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0m;
        }

        var letters = text.Count(char.IsLetter);
        var digits = text.Count(char.IsDigit);
        var symbols = text.Count(c => !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c) && c is not ':' and not '\'' and not '-' and not '&');
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tokenDensity = tokens.Length / (decimal)Math.Max(1, text.Length / 4);

        decimal score = 0.1m;
        score += decimal.Min(0.25m, letters / (decimal)Math.Max(1, text.Length));
        score += decimal.Min(0.1m, digits / (decimal)Math.Max(10, text.Length));
        score += decimal.Min(0.2m, tokenDensity);
        score -= decimal.Min(0.22m, symbols / (decimal)Math.Max(1, text.Length));
        var likelyTitle = InferTitleFromOcr(text, string.Empty, string.Empty);
        if (!IsGenericTitleCandidate(likelyTitle))
        {
            score += 0.2m;
        }

        return decimal.Max(0m, decimal.Min(score, 1m));
    }

    private static string BuildBankSeed(string titleFromOcr, string ocrText)
    {
        var bestLine = ocrText
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeWhitespace)
            .Where(line => line.Length is >= 4 and <= 64)
            .OrderByDescending(ScoreTitleCandidate)
            .FirstOrDefault() ?? string.Empty;

        var compactOcr = NormalizeWhitespace(ocrText);
        if (compactOcr.Length > 320)
        {
            compactOcr = compactOcr[..320];
        }

        var fragments = new[]
            {
                NormalizeSeedFragment(titleFromOcr),
                NormalizeSeedFragment(bestLine),
                NormalizeSeedFragment(compactOcr)
            }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Where(x => !IsGenericTitleCandidate(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (fragments.Count == 0)
        {
            return string.Empty;
        }

        // Drop fragments fully covered by a longer fragment to avoid repeated seed spam.
        fragments = fragments
            .Where(fragment => !fragments.Any(other =>
                !ReferenceEquals(fragment, other) &&
                other.Length > fragment.Length &&
                other.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var seed = string.Join(' ', fragments).Trim();
        if (seed.Length > 320)
        {
            seed = seed[..320];
        }

        return seed;
    }

    private async Task<BankSeedSelection?> ResolveBestBankMatchAsync(
        IReadOnlyList<OcrTextSignal> ocrSignals,
        string mergedOcrText,
        string mergedTitleFromOcr,
        string platformHint,
        CancellationToken cancellationToken)
    {
        var seenSeeds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var evaluations = new List<BankSeedSelection>();

        foreach (var signal in ocrSignals.OrderByDescending(item => item.OcrQuality))
        {
            var signalTitle = InferTitleFromOcr(signal.Text, mergedTitleFromOcr, string.Empty);
            var seed = BuildBankSeed(signalTitle, signal.Text);
            if (string.IsNullOrWhiteSpace(seed) || !seenSeeds.Add(seed))
            {
                continue;
            }

            var evaluation = await EvaluateBankSeedAsync(
                seed,
                signalTitle,
                signal.Text,
                platformHint,
                signal.OcrQuality,
                cancellationToken);
            if (evaluation is not null)
            {
                evaluations.Add(evaluation);
            }
        }

        var mergedSeed = BuildBankSeed(mergedTitleFromOcr, mergedOcrText);
        if (!string.IsNullOrWhiteSpace(mergedSeed) && seenSeeds.Add(mergedSeed))
        {
            var mergedEvaluation = await EvaluateBankSeedAsync(
                mergedSeed,
                mergedTitleFromOcr,
                mergedOcrText,
                platformHint,
                0.6m,
                cancellationToken);
            if (mergedEvaluation is not null)
            {
                evaluations.Add(mergedEvaluation);
            }
        }

        return evaluations
            .OrderByDescending(item => item.Confidence)
            .ThenByDescending(item => item.Match.Score)
            .FirstOrDefault();
    }

    private async Task<BankSeedSelection?> EvaluateBankSeedAsync(
        string seed,
        string signalTitle,
        string signalText,
        string platformHint,
        decimal signalQuality,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(seed))
        {
            return null;
        }

        var candidates = await _titleBankService.SearchAsync(seed, platformHint, 5, cancellationToken);
        if (candidates.Count == 0)
        {
            return null;
        }

        BankSeedSelection? best = null;
        foreach (var candidate in candidates)
        {
            var overlap = ScoreTitleOverlap(signalTitle, candidate.Entry.Title);
            var confidence = ScoreBankCandidateConfidence(candidate, overlap, signalTitle, signalText, signalQuality);
            var evaluation = new BankSeedSelection(seed, signalTitle, signalQuality, candidate, confidence);

            if (best is null || evaluation.Confidence > best.Confidence)
            {
                best = evaluation;
            }
        }

        return best;
    }

    private static decimal ScoreBankCandidateConfidence(
        GameTitleMatchResult candidate,
        decimal titleOverlap,
        string signalTitle,
        string signalText,
        decimal signalQuality)
    {
        var confidence = (candidate.Score * 0.58m) + (titleOverlap * 0.28m) + (signalQuality * 0.14m);
        if (titleOverlap >= 0.65m)
        {
            confidence += 0.08m;
        }
        else if (titleOverlap <= 0.18m)
        {
            confidence -= 0.12m;
        }

        if (ContainsPhrase(signalTitle, "among thieves") &&
            !ContainsPhrase(candidate.Entry.Title, "among thieves"))
        {
            confidence -= 0.22m;
        }

        if (LooksLikeAddonTitle(candidate.Entry.Title) &&
            !LooksLikeAddonContext(signalTitle, signalText))
        {
            confidence -= 0.18m;
        }

        if (HasNumericToken(signalTitle) && !SharesNumericToken(signalTitle, candidate.Entry.Title))
        {
            confidence -= 0.08m;
        }

        return decimal.Max(0m, decimal.Min(1m, confidence));
    }

    private static decimal ScoreTitleOverlap(string source, string candidate)
    {
        var sourceTokens = TokenizeAlphaNumeric(source);
        var candidateTokens = TokenizeAlphaNumeric(candidate);
        if (sourceTokens.Length == 0 || candidateTokens.Length == 0)
        {
            return 0m;
        }

        var overlap = sourceTokens.Count(token => candidateTokens.Contains(token, StringComparer.Ordinal));
        return (decimal)overlap / decimal.Max(sourceTokens.Length, candidateTokens.Length);
    }

    private static string[] TokenizeAlphaNumeric(string value)
    {
        return value
            .ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => new string(token.Where(char.IsLetterOrDigit).ToArray()))
            .Where(token => token.Length >= 2)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static bool ContainsPhrase(string value, string phrase)
    {
        if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(phrase))
        {
            return false;
        }

        var normalizedValue = NormalizeWhitespace(value).ToLowerInvariant();
        var normalizedPhrase = NormalizeWhitespace(phrase).ToLowerInvariant();
        return normalizedValue.Contains(normalizedPhrase, StringComparison.Ordinal);
    }

    private static bool LooksLikeAddonTitle(string value)
    {
        var normalized = NormalizeWhitespace(value).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        return normalized.Contains("pack", StringComparison.Ordinal) ||
               normalized.Contains("dlc", StringComparison.Ordinal) ||
               normalized.Contains("expansion", StringComparison.Ordinal) ||
               normalized.Contains("bundle", StringComparison.Ordinal) ||
               normalized.Contains("season pass", StringComparison.Ordinal) ||
               normalized.Contains("multiplayer", StringComparison.Ordinal);
    }

    private static bool LooksLikeAddonContext(string signalTitle, string signalText)
    {
        return LooksLikeAddonTitle(signalTitle) || LooksLikeAddonTitle(signalText);
    }

    private static bool HasNumericToken(string value)
    {
        return TokenizeAlphaNumeric(value).Any(token => token.All(char.IsDigit));
    }

    private static bool SharesNumericToken(string source, string candidate)
    {
        var sourceNumbers = TokenizeAlphaNumeric(source)
            .Where(token => token.All(char.IsDigit))
            .ToHashSet(StringComparer.Ordinal);
        if (sourceNumbers.Count == 0)
        {
            return true;
        }

        var candidateNumbers = TokenizeAlphaNumeric(candidate)
            .Where(token => token.All(char.IsDigit))
            .ToHashSet(StringComparer.Ordinal);
        return sourceNumbers.Overlaps(candidateNumbers);
    }

    private static string NormalizeSeedFragment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var cleaned = SanitizeOcrText(value).ToLowerInvariant();
        cleaned = new string(cleaned
            .Select(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) ? c : ' ')
            .ToArray());

        var tokens = NormalizeWhitespace(cleaned)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(18)
            .ToArray();
        tokens = StripLeadingPlayStationBannerCluster(tokens);

        if (tokens.Length == 0)
        {
            return string.Empty;
        }

        var normalizedTokens = new List<string>();
        foreach (var token in tokens)
        {
            if (SeedNoiseTokens.Contains(token) || TitleNoiseTokens.Contains(token) || IsSeedPlatformNoiseToken(token))
            {
                continue;
            }

            foreach (var expanded in ExpandMergedSeedToken(token))
            {
                if (string.IsNullOrWhiteSpace(expanded) || IsLowSignalSeedToken(expanded))
                {
                    continue;
                }

                if (!normalizedTokens.Any(existing => IsNearDuplicateToken(existing, expanded)))
                {
                    normalizedTokens.Add(expanded);
                }
            }
        }

        if (normalizedTokens.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(' ', normalizedTokens);
    }

    private static string[] StripLeadingPlayStationBannerCluster(string[] tokens)
    {
        if (tokens.Length < 3)
        {
            return tokens;
        }

        var scanLimit = Math.Min(tokens.Length, 8);
        var playStationIndex = -1;
        var networkIndex = -1;

        for (var i = 0; i < scanLimit; i++)
        {
            var token = tokens[i];
            if (playStationIndex < 0 && IsPlayStationHeaderToken(token))
            {
                playStationIndex = i;
            }

            if (networkIndex < 0 && IsNetworkHeaderToken(token))
            {
                networkIndex = i;
            }
        }

        if (playStationIndex < 0 || networkIndex < 0)
        {
            return tokens;
        }

        if (Math.Abs(playStationIndex - networkIndex) > 4)
        {
            return tokens;
        }

        var cut = Math.Max(playStationIndex, networkIndex);
        while (cut + 1 < scanLimit && IsLikelyPlayStationBannerToken(tokens[cut + 1]))
        {
            cut++;
        }

        if (cut + 1 >= tokens.Length)
        {
            return [];
        }

        return tokens[(cut + 1)..];
    }

    private static bool IsPlayStationHeaderToken(string token)
    {
        var compact = token.Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(compact))
        {
            return false;
        }

        return compact.Contains("playstation", StringComparison.Ordinal) ||
               compact.Contains("piaystation", StringComparison.Ordinal) ||
               compact.Equals("station", StringComparison.Ordinal) ||
               compact.StartsWith("play", StringComparison.Ordinal) ||
               compact.EndsWith("station", StringComparison.Ordinal);
    }

    private static bool IsNetworkHeaderToken(string token)
    {
        var compact = token.Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(compact))
        {
            return false;
        }

        return compact.Contains("network", StringComparison.Ordinal) ||
               compact.Equals("psn", StringComparison.Ordinal);
    }

    private static bool IsLikelyPlayStationBannerToken(string token)
    {
        var compact = token.Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(compact))
        {
            return false;
        }

        if (IsPlayStationHeaderToken(compact) || IsNetworkHeaderToken(compact))
        {
            return true;
        }

        return compact is "entertainment" or "supplement" or "ment" or "ent" or "service" or "only" or "on";
    }

    private static bool IsSeedPlatformNoiseToken(string token)
    {
        var compact = token.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(compact))
        {
            return false;
        }

        if (compact.Contains("playstation", StringComparison.Ordinal) ||
            compact.Contains("piaystation", StringComparison.Ordinal) ||
            compact.Contains("xbox", StringComparison.Ordinal))
        {
            return true;
        }

        return compact.Equals("ps3", StringComparison.Ordinal) ||
               compact.Equals("ps4", StringComparison.Ordinal) ||
               compact.Equals("ps5", StringComparison.Ordinal) ||
               compact.Equals("p53", StringComparison.Ordinal) ||
               compact.Equals("p54", StringComparison.Ordinal) ||
               compact.Equals("p55", StringComparison.Ordinal) ||
               compact.EndsWith("ps3", StringComparison.Ordinal) ||
               compact.EndsWith("ps4", StringComparison.Ordinal) ||
               compact.EndsWith("ps5", StringComparison.Ordinal) ||
               compact.Equals("x360", StringComparison.Ordinal) ||
               compact.Equals("xone", StringComparison.Ordinal) ||
               compact.Equals("xbox360", StringComparison.Ordinal) ||
               compact.Equals("xboxone", StringComparison.Ordinal) ||
               SeedPlatformNoiseRegex().IsMatch(compact);
    }

    private static IEnumerable<string> ExpandMergedSeedToken(string token)
    {
        if (token.Length <= 5 && token.Any(char.IsDigit) && token.Any(char.IsLetter))
        {
            yield break;
        }

        var mapped = token
            .Replace('0', 'o')
            .Replace('1', 'i')
            .Replace('3', 'e')
            .Replace('4', 'a')
            .Replace('5', 's');

        yield return mapped;
    }

    private static bool IsLowSignalSeedToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return true;
        }

        if (token.Length < 3)
        {
            return true;
        }

        var digits = token.Count(char.IsDigit);
        var letters = token.Count(char.IsLetter);
        if (digits > 0 && letters > 0 && token.Length <= 5)
        {
            return true;
        }

        return digits > token.Length / 2;
    }

    private static bool IsNearDuplicateToken(string first, string second)
    {
        if (first.Equals(second, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (first.Length >= 5 && second.Length >= 5 &&
            (first.Contains(second, StringComparison.OrdinalIgnoreCase) ||
             second.Contains(first, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (first.Length >= 6 &&
            second.Length >= 6 &&
            Math.Abs(first.Length - second.Length) <= 2)
        {
            return TokenEditDistance(first.ToLowerInvariant(), second.ToLowerInvariant()) <= 2;
        }

        return false;
    }

    private static int TokenEditDistance(string a, string b)
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

    private string ResolvePhotoAbsolutePath(string relativeUrl)
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

    private static string InferTitleFromOcr(string ocrText, string fallbackTitle, string userTitle)
    {
        var candidates = ExtractTitleCandidatesFromOcr(ocrText);

        if (candidates.Count == 0)
        {
            var fallbackFromOcr = BuildFallbackTitleFromOcr(ocrText);
            if (!string.IsNullOrWhiteSpace(fallbackFromOcr))
            {
                return fallbackFromOcr;
            }

            if (!string.IsNullOrWhiteSpace(userTitle))
            {
                return userTitle;
            }

            if (!IsGenericTitleCandidate(fallbackTitle))
            {
                return fallbackTitle;
            }

            return "Unknown Video Game";
        }

        var best = candidates
            .OrderByDescending(ScoreTitleCandidate)
            .ThenByDescending(line => line.Length)
            .First();

        if (best.Length < 4 || IsGenericTitleCandidate(best))
        {
            var fallbackFromOcr = BuildFallbackTitleFromOcr(ocrText);
            if (!string.IsNullOrWhiteSpace(fallbackFromOcr))
            {
                return fallbackFromOcr;
            }
        }

        return best;
    }

    private static string InferPlatformFromOcr(string ocrText, string fallbackPlatform, string userPlatform)
    {
        var searchable = ocrText.ToLowerInvariant();
        var compact = searchable.Replace(" ", string.Empty, StringComparison.Ordinal);

        if (Ps3LooseRegex().IsMatch(compact))
        {
            return "PlayStation 3";
        }

        if (Ps4LooseRegex().IsMatch(compact))
        {
            return "PlayStation 4";
        }

        if (Ps5LooseRegex().IsMatch(compact))
        {
            return "PlayStation 5";
        }

        if (Xbox360LooseRegex().IsMatch(compact))
        {
            return "Xbox 360";
        }

        foreach (var (platform, keywords) in PlatformKeywords)
        {
            if (keywords.Any(searchable.Contains))
            {
                return platform;
            }
        }

        return string.IsNullOrWhiteSpace(userPlatform) ? fallbackPlatform : userPlatform;
    }

    private static string InferConditionFromOcr(string ocrText, string fallbackCondition)
    {
        if (ocrText.Contains("sealed", StringComparison.OrdinalIgnoreCase) ||
            ocrText.Contains("brand new", StringComparison.OrdinalIgnoreCase))
        {
            return "New";
        }

        return fallbackCondition;
    }

    private static bool ContainsBlockedTerm(string value)
    {
        var lower = value.ToLowerInvariant();
        return BlockedTitleTerms.Any(lower.Contains);
    }

    private static int ScoreTitleCandidate(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || IsGenericTitleCandidate(line))
        {
            return int.MinValue / 4;
        }

        var score = 0;
        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is >= 1 and <= 7)
        {
            score += 4;
        }

        if (char.IsLetter(line[0]))
        {
            score += 2;
        }

        if (words.Count(w => w.Length >= 3) >= 2)
        {
            score += 2;
        }

        var uppercaseRatio = line.Count(char.IsUpper) / (double)Math.Max(1, line.Count(char.IsLetter));
        if (uppercaseRatio is > 0.15 and < 0.95)
        {
            score += 2;
        }

        var symbolCount = line.Count(c => !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c));
        if (symbolCount <= 3)
        {
            score += 1;
        }

        var digitRatio = line.Count(char.IsDigit) / (double)Math.Max(1, line.Length);
        if (digitRatio > 0.18)
        {
            score -= 2;
        }

        if (words.Count(w => w.Length >= 3) < 2)
        {
            score -= 3;
        }

        return score;
    }

    private static List<string> ExtractTitleCandidatesFromOcr(string ocrText)
    {
        var cleaned = SanitizeOcrText(ocrText);
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return [];
        }

        var rawCandidates = cleaned
            .Split(['\r', '\n', '.', '|', ';', ':', '!', '?', '/', '\\', '(', ')', '[', ']'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeTitleCandidate)
            .Where(candidate => candidate.Length is >= 4 and <= 64)
            .ToList();

        var fallback = BuildFallbackTitleFromOcr(cleaned);
        if (!string.IsNullOrWhiteSpace(fallback))
        {
            rawCandidates.Add(fallback);
        }

        return rawCandidates
            .Where(candidate => candidate.Any(char.IsLetter))
            .Where(candidate => !ContainsBlockedTerm(candidate))
            .Where(candidate => !IsGenericTitleCandidate(candidate))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizeTitleCandidate(string value)
    {
        var normalized = NormalizeSeedFragment(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        return ToTitleCaseWords(normalized);
    }

    private static string BuildFallbackTitleFromOcr(string ocrText)
    {
        var normalized = NormalizeSeedFragment(ocrText);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        var distinctTokens = new List<string>();
        foreach (var token in normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TitleNoiseTokens.Contains(token) || IsLowSignalSeedToken(token))
            {
                continue;
            }

            if (distinctTokens.Any(existing => IsNearDuplicateToken(existing, token)))
            {
                continue;
            }

            distinctTokens.Add(token);
            if (distinctTokens.Count >= 6)
            {
                break;
            }
        }

        if (distinctTokens.Count == 1 && IsStrongSingleTokenTitle(distinctTokens[0]))
        {
            return ToTitleCaseWords(distinctTokens[0]);
        }

        if (distinctTokens.Count < 2)
        {
            return string.Empty;
        }

        var phrase = string.Join(' ', distinctTokens);
        if (IsGenericTitleCandidate(phrase))
        {
            return string.Empty;
        }

        return ToTitleCaseWords(phrase);
    }

    private static bool IsGenericTitleCandidate(string value)
    {
        var normalized = NormalizeWhitespace(value).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return true;
        }

        if (normalized is "video game" or "video games" or "game" or "games" or "unknown video game")
        {
            return true;
        }

        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return true;
        }

        if (tokens.Length == 1)
        {
            return !IsStrongSingleTokenTitle(tokens[0]);
        }

        var meaningful = tokens.Count(token =>
            token.Length >= 3 &&
            !SeedNoiseTokens.Contains(token) &&
            !TitleNoiseTokens.Contains(token) &&
            !IsSeedPlatformNoiseToken(token));
        return meaningful < 2;
    }

    private static bool IsReliableOcrTitle(string value)
    {
        if (IsGenericTitleCandidate(value))
        {
            return false;
        }

        if (LooksLikeNoisyListingText(value))
        {
            return false;
        }

        var score = ScoreTitleCandidate(value);
        if (score < 5)
        {
            return false;
        }

        var tokens = NormalizeWhitespace(value)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return false;
        }

        if (tokens.Length == 1)
        {
            return IsStrongSingleTokenTitle(tokens[0]);
        }

        var longTokens = tokens.Count(token => token.Length >= 4);
        if (longTokens < 2)
        {
            return false;
        }

        var shortTokenRatio = tokens.Count(token => token.Length <= 3) / (double)tokens.Length;
        if (shortTokenRatio > 0.55)
        {
            return false;
        }

        var avgTokenLength = tokens.Average(token => token.Length);
        return avgTokenLength >= 3.8 || tokens.Any(token => token.Length >= 8);
    }

    private static bool IsStrongSingleTokenTitle(string token)
    {
        var normalized = NormalizeWhitespace(token).ToLowerInvariant();
        if (normalized.Length is < 5 or > 24)
        {
            return false;
        }

        if (!normalized.All(char.IsLetterOrDigit))
        {
            return false;
        }

        if (TitleNoiseTokens.Contains(normalized) ||
            SeedNoiseTokens.Contains(normalized) ||
            IsSeedPlatformNoiseToken(normalized) ||
            ContainsBlockedTerm(normalized) ||
            IsLikelyPlayStationBannerToken(normalized))
        {
            return false;
        }

        var letters = normalized.Count(char.IsLetter);
        if (letters < normalized.Length - 1)
        {
            return false;
        }

        var vowelCount = normalized.Count(c => c is 'a' or 'e' or 'i' or 'o' or 'u');
        if (vowelCount < 2)
        {
            return false;
        }

        if (MaxConsonantRun(normalized) >= 5)
        {
            return false;
        }

        return true;
    }

    private static int MaxConsonantRun(string token)
    {
        var maxRun = 0;
        var currentRun = 0;

        foreach (var c in token)
        {
            if (!char.IsLetter(c))
            {
                currentRun = 0;
                continue;
            }

            if (c is 'a' or 'e' or 'i' or 'o' or 'u')
            {
                currentRun = 0;
                continue;
            }

            currentRun++;
            if (currentRun > maxRun)
            {
                maxRun = currentRun;
            }
        }

        return maxRun;
    }

    private static string BuildSafeFallbackTitle(string userTitle, string ocrPlatform, string fallbackPlatform)
    {
        var cleanedUserTitle = NormalizeWhitespace(userTitle);
        if (IsUserTitleUsable(cleanedUserTitle))
        {
            return cleanedUserTitle;
        }

        var platform = string.IsNullOrWhiteSpace(ocrPlatform) ? fallbackPlatform : ocrPlatform;
        return string.IsNullOrWhiteSpace(platform) ? "Video Game" : $"Video Game ({platform})";
    }

    private static bool IsUserTitleUsable(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (IsGenericTitleCandidate(value) || LooksLikeNoisyListingText(value))
        {
            return false;
        }

        var tokens = value
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var meaningful = tokens.Count(token => token.Length >= 3);
        return meaningful >= 2;
    }

    private static bool LooksLikeNoisyListingText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var normalized = NormalizeWhitespace(value).ToLowerInvariant();
        if (normalized.Contains("playstation-network", StringComparison.Ordinal) ||
            (normalized.Contains("station", StringComparison.Ordinal) && normalized.Contains("network", StringComparison.Ordinal)) ||
            normalized.Contains("only on playstation", StringComparison.Ordinal) ||
            normalized.Contains("online interactions", StringComparison.Ordinal) ||
            normalized.Contains("not rated", StringComparison.Ordinal) ||
            normalized.Contains("raned by", StringComparison.Ordinal))
        {
            return true;
        }

        var tokens = normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length >= 8)
        {
            var shortOrNumericTokens = tokens.Count(token => token.Length <= 3 || token.All(char.IsDigit));
            if (shortOrNumericTokens / (double)tokens.Length >= 0.55)
            {
                return true;
            }
        }

        var noiseHits = tokens.Count(token => TitleNoiseTokens.Contains(token));
        return noiseHits >= 3;
    }

    private static string ToTitleCaseWords(string value)
    {
        var tokens = value
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token =>
            {
                if (token.Length <= 1)
                {
                    return token.ToUpperInvariant();
                }

                if (token.All(char.IsDigit))
                {
                    return token;
                }

                return char.ToUpperInvariant(token[0]) + token[1..];
            });
        return string.Join(' ', tokens);
    }

    private static string NormalizeWhitespace(string value)
    {
        return CollapseWhitespaceRegex().Replace(value, " ").Trim();
    }

    private static string SanitizeOcrText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var chars = value
            .Select(c => c <= 127 ? c : ' ')
            .Select(c => c is '|' or '~' or '*' or '^' or '`' ? ' ' : c)
            .ToArray();
        var cleaned = new string(chars);
        cleaned = NoiseTokenRegex().Replace(cleaned, " ");
        return NormalizeWhitespace(cleaned);
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private sealed record OcrTextSignal(
        string ImagePath,
        string Text,
        decimal OcrQuality);

    private sealed record BankSeedSelection(
        string Seed,
        string SignalTitle,
        decimal SignalQuality,
        GameTitleMatchResult Match,
        decimal Confidence);

    private sealed record ImageVariantBundle(
        string OriginalImagePath,
        IReadOnlyList<string> Variants,
        string TitleVariantPath,
        string PlatformVariantPath,
        IReadOnlyList<string> AllPaths);

    [GeneratedRegex(@"[\u0000-\u001F]")]
    private static partial Regex NoiseTokenRegex();

    [GeneratedRegex(@"p[a-z0-9]{0,2}s[a-z0-9]{0,2}3")]
    private static partial Regex Ps3LooseRegex();

    [GeneratedRegex(@"p[a-z0-9]{0,2}s[a-z0-9]{0,2}4")]
    private static partial Regex Ps4LooseRegex();

    [GeneratedRegex(@"p[a-z0-9]{0,2}s[a-z0-9]{0,2}5")]
    private static partial Regex Ps5LooseRegex();

    [GeneratedRegex(@"x[a-z0-9]{0,2}b[a-z0-9]{0,2}o[a-z0-9]{0,2}x[a-z0-9]{0,2}3[a-z0-9]{0,2}6[a-z0-9]{0,2}0")]
    private static partial Regex Xbox360LooseRegex();

    [GeneratedRegex(@"^(ps?\d|p\d\d|xbox|x\d+|switch|wii|n64|snes|nes)$")]
    private static partial Regex SeedPlatformNoiseRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex CollapseWhitespaceRegex();
}
