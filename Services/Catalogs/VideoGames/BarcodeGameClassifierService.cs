using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CatalogPilot.Models;
using CatalogPilot.Options;
using Microsoft.Extensions.Options;

namespace CatalogPilot.Services;

public sealed class BarcodeGameClassifierService : IBarcodeGameClassifierService
{
    private readonly LocalClassifierOptions _localOptions;
    private readonly IWebHostEnvironment _hostEnvironment;
    private readonly RuleBasedVideoGameClassifierService _fallbackClassifier;
    private readonly IGameCatalogStore _catalogStore;
    private readonly IGameCodeBankService _codeBankService;
    private readonly IExternalBarcodeLookupService _externalBarcodeLookupService;
    private readonly ILogger<BarcodeGameClassifierService> _logger;
    private bool _missingBarcodeRuntimeLogged;
    private bool _missingPyzbarRuntimeLogged;

    public BarcodeGameClassifierService(
        IOptions<LocalClassifierOptions> localOptions,
        IWebHostEnvironment hostEnvironment,
        RuleBasedVideoGameClassifierService fallbackClassifier,
        IGameCatalogStore catalogStore,
        IGameCodeBankService codeBankService,
        IExternalBarcodeLookupService externalBarcodeLookupService,
        ILogger<BarcodeGameClassifierService> logger)
    {
        _localOptions = localOptions.Value;
        _hostEnvironment = hostEnvironment;
        _fallbackClassifier = fallbackClassifier;
        _catalogStore = catalogStore;
        _codeBankService = codeBankService;
        _externalBarcodeLookupService = externalBarcodeLookupService;
        _logger = logger;
    }

    public async Task<ClassificationResult?> TryClassifyAsync(ListingInput input, CancellationToken cancellationToken = default)
    {
        if (!_localOptions.Enabled || input.Photos.Count == 0)
        {
            return null;
        }

        var detectedCodes = await ScanBarcodesAsync(input, cancellationToken);
        if (detectedCodes.Count == 0)
        {
            return null;
        }
        var lookupCodes = ExpandBarcodeVariants(detectedCodes);
        if (lookupCodes.Count == 0)
        {
            return null;
        }

        var fallback = await _fallbackClassifier.ClassifyAsync(input, cancellationToken);
        await _catalogStore.InitializeAsync(cancellationToken);

        var cachedMatch = await FindCachedBarcodeMatchAsync(lookupCodes, input.Platform, cancellationToken);
        if (cachedMatch is not null)
        {
            return BuildCatalogBarcodeMatchedClassification(fallback, cachedMatch, detectedCodes);
        }

        var localMatch = _codeBankService.FindBestMatch(lookupCodes, input.Platform);
        if (localMatch is not null)
        {
            var localResult = BuildLocalCodeMatchedClassification(fallback, localMatch, detectedCodes);
            await CacheLocalCodeMatchAsync(localMatch, lookupCodes, cancellationToken);
            return localResult;
        }

        var externalMatch = await FindExternalCodeMatchAsync(lookupCodes, input.Platform, cancellationToken);
        if (externalMatch is not null)
        {
            var externalResult = BuildExternalCodeMatchedClassification(fallback, externalMatch, detectedCodes);
            await CacheExternalCodeMatchAsync(externalMatch, lookupCodes, cancellationToken);
            return externalResult;
        }

        return null;
    }

    private async Task<IReadOnlyList<string>> ScanBarcodesAsync(ListingInput input, CancellationToken cancellationToken)
    {
        var imagePaths = input.Photos
            .Select(photo => ResolvePhotoAbsolutePath(photo.RelativeUrl))
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .ToArray();
        if (imagePaths.Length == 0)
        {
            return [];
        }

        var pythonExe = ResolvePythonExecutable();
        var scriptPath = ResolveBarcodeScriptPath();
        if (string.IsNullOrWhiteSpace(pythonExe) || string.IsNullOrWhiteSpace(scriptPath))
        {
            if (!_missingBarcodeRuntimeLogged)
            {
                _logger.LogWarning(
                    "Barcode scanner runtime unavailable. Configure LocalClassifier:PythonExecutablePath / BarcodeScriptPath.");
                _missingBarcodeRuntimeLogged = true;
            }

            return [];
        }

        foreach (var imagePath in imagePaths)
        {
            var codes = await RunBarcodeScanAsync(
                pythonExe,
                scriptPath,
                [imagePath],
                cancellationToken);
            if (codes.Count > 0)
            {
                return codes;
            }
        }

        return [];
    }

    private async Task<IReadOnlyList<string>> RunBarcodeScanAsync(
        string pythonExe,
        string scriptPath,
        IReadOnlyList<string> imagePaths,
        CancellationToken cancellationToken)
    {
        if (imagePaths.Count == 0)
        {
            return [];
        }

        var timeoutSeconds = Math.Clamp(_localOptions.ProcessTimeoutSeconds, 5, 120);
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var args = new StringBuilder();
        args.Append('"').Append(scriptPath).Append('"');
        foreach (var path in imagePaths)
        {
            args.Append(" --image \"").Append(path).Append('"');
        }

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

            if (process.ExitCode != 0)
            {
                _logger.LogDebug("Barcode scanner script failed ({ExitCode}): {Error}", process.ExitCode, Truncate(stderr, 280));
                return [];
            }

            var scannerWarning = ExtractScannerWarning(stdout);
            if (!string.IsNullOrWhiteSpace(scannerWarning))
            {
                _logger.LogDebug("Barcode scanner warning: {Warning}", Truncate(scannerWarning, 280));
                if (!_missingPyzbarRuntimeLogged &&
                    scannerWarning.Contains("pyzbar import failed", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning(
                        "Barcode scanner running without pyzbar. Install pyzbar in the configured Python runtime for better UPC/EAN detection.");
                    _missingPyzbarRuntimeLogged = true;
                }
            }

            return ParseBarcodeOutput(stdout);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Barcode scanner process timed out");
            return [];
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Barcode scanner invocation failed");
            return [];
        }
    }

    private async Task<ExternalBarcodeLookupResult?> FindExternalCodeMatchAsync(
        IReadOnlyList<string> detectedCodes,
        string? platformHint,
        CancellationToken cancellationToken)
    {
        foreach (var code in detectedCodes.Take(6))
        {
            var match = await _externalBarcodeLookupService.LookupAsync(code, platformHint, cancellationToken);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private async Task<CatalogBarcodeMatchResult?> FindCachedBarcodeMatchAsync(
        IReadOnlyList<string> detectedCodes,
        string? platformHint,
        CancellationToken cancellationToken)
    {
        foreach (var code in detectedCodes.Take(8))
        {
            var match = await _catalogStore.FindByBarcodeAsync(code, platformHint, cancellationToken);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private async Task CacheLocalCodeMatchAsync(
        GameCodeMatchResult localMatch,
        IReadOnlyList<string> detectedCodes,
        CancellationToken cancellationToken)
    {
        var entry = new GameTitleBankEntry
        {
            Title = localMatch.Entry.Title,
            Platform = localMatch.Entry.Platform,
            Franchise = localMatch.Entry.Franchise,
            Aliases = localMatch.Entry.Aliases
        };

        await _catalogStore.UpsertTitlesAsync([entry], "local-code-bank", cancellationToken);
        foreach (var code in detectedCodes.Take(8))
        {
            await _catalogStore.UpsertBarcodeAsync(code, entry, "local-code-bank", localMatch.Score, cancellationToken);
        }
    }

    private async Task CacheExternalCodeMatchAsync(
        ExternalBarcodeLookupResult externalMatch,
        IReadOnlyList<string> detectedCodes,
        CancellationToken cancellationToken)
    {
        var entry = new GameTitleBankEntry
        {
            Title = externalMatch.Title,
            Platform = externalMatch.Platform,
            Franchise = externalMatch.Franchise,
            Aliases = []
        };

        await _catalogStore.UpsertTitlesAsync([entry], externalMatch.Provider, cancellationToken);
        foreach (var code in detectedCodes.Take(8))
        {
            await _catalogStore.UpsertBarcodeAsync(code, entry, externalMatch.Provider, externalMatch.Confidence, cancellationToken);
        }
    }

    private ClassificationResult BuildLocalCodeMatchedClassification(
        ClassificationResult fallback,
        GameCodeMatchResult codeMatch,
        IReadOnlyCollection<string> detectedCodes)
    {
        var entry = codeMatch.Entry;
        var platform = !string.IsNullOrWhiteSpace(entry.Platform) ? entry.Platform : fallback.SuggestedPlatform;
        var specifics = new Dictionary<string, string>(fallback.ItemSpecifics, StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(platform))
        {
            specifics["Platform"] = platform;
        }

        specifics["Detected Game Code"] = codeMatch.MatchedCode;
        if (detectedCodes.Count > 1)
        {
            specifics["Detected Game Codes"] = string.Join(", ", detectedCodes.Take(6));
        }

        return new ClassificationResult
        {
            Source = "Barcode + Local Code Bank + Rule-based",
            SuggestedTitle = entry.Title,
            SuggestedPlatform = platform,
            BankMatchedTitle = entry.Title,
            BankMatchedPlatform = platform,
            BankMatchScore = codeMatch.Score,
            SuggestedCondition = fallback.SuggestedCondition,
            Franchise = string.IsNullOrWhiteSpace(entry.Franchise) ? fallback.Franchise : entry.Franchise,
            Edition = fallback.Edition,
            CategoryId = fallback.CategoryId,
            Confidence = decimal.Min(0.99m, decimal.Max(0.88m, fallback.Confidence + 0.35m)),
            ItemSpecifics = specifics
        };
    }

    private ClassificationResult BuildExternalCodeMatchedClassification(
        ClassificationResult fallback,
        ExternalBarcodeLookupResult externalMatch,
        IReadOnlyCollection<string> detectedCodes)
    {
        var platform = !string.IsNullOrWhiteSpace(externalMatch.Platform)
            ? externalMatch.Platform
            : fallback.SuggestedPlatform;

        var specifics = new Dictionary<string, string>(fallback.ItemSpecifics, StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(platform))
        {
            specifics["Platform"] = platform;
        }

        if (!string.IsNullOrWhiteSpace(externalMatch.Code))
        {
            specifics["Detected Game Code"] = externalMatch.Code;
        }

        if (detectedCodes.Count > 1)
        {
            specifics["Detected Game Codes"] = string.Join(", ", detectedCodes.Take(6));
        }

        if (!string.IsNullOrWhiteSpace(externalMatch.Provider))
        {
            specifics["Barcode Provider"] = externalMatch.Provider;
        }

        if (!string.IsNullOrWhiteSpace(externalMatch.Brand))
        {
            specifics["Publisher/Brand"] = externalMatch.Brand;
        }

        return new ClassificationResult
        {
            Source = "Barcode + External Lookup + Rule-based",
            SuggestedTitle = externalMatch.Title,
            SuggestedPlatform = platform,
            BankMatchedTitle = externalMatch.Title,
            BankMatchedPlatform = platform,
            BankMatchScore = externalMatch.Confidence,
            SuggestedCondition = fallback.SuggestedCondition,
            Franchise = string.IsNullOrWhiteSpace(externalMatch.Franchise) ? fallback.Franchise : externalMatch.Franchise,
            Edition = fallback.Edition,
            CategoryId = fallback.CategoryId,
            Confidence = decimal.Min(0.98m, decimal.Max(0.82m, fallback.Confidence + (externalMatch.Confidence * 0.3m))),
            ItemSpecifics = specifics
        };
    }

    private ClassificationResult BuildCatalogBarcodeMatchedClassification(
        ClassificationResult fallback,
        CatalogBarcodeMatchResult cachedMatch,
        IReadOnlyCollection<string> detectedCodes)
    {
        var entry = cachedMatch.Match.Entry;
        var platform = !string.IsNullOrWhiteSpace(entry.Platform) ? entry.Platform : fallback.SuggestedPlatform;
        var specifics = new Dictionary<string, string>(fallback.ItemSpecifics, StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(platform))
        {
            specifics["Platform"] = platform;
        }

        specifics["Detected Game Code"] = cachedMatch.Code;
        if (detectedCodes.Count > 1)
        {
            specifics["Detected Game Codes"] = string.Join(", ", detectedCodes.Take(6));
        }

        return new ClassificationResult
        {
            Source = "Barcode + Local Catalog Cache + Rule-based",
            SuggestedTitle = entry.Title,
            SuggestedPlatform = platform,
            BankMatchedTitle = entry.Title,
            BankMatchedPlatform = platform,
            BankMatchScore = cachedMatch.Match.Score,
            SuggestedCondition = fallback.SuggestedCondition,
            Franchise = string.IsNullOrWhiteSpace(entry.Franchise) ? fallback.Franchise : entry.Franchise,
            Edition = fallback.Edition,
            CategoryId = fallback.CategoryId,
            Confidence = decimal.Min(0.99m, decimal.Max(0.86m, fallback.Confidence + 0.30m)),
            ItemSpecifics = specifics
        };
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

    private string ResolvePythonExecutable()
    {
        var configured = Environment.ExpandEnvironmentVariables((_localOptions.PythonExecutablePath ?? string.Empty).Trim());
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

    private string ResolveBarcodeScriptPath()
    {
        var configured = Environment.ExpandEnvironmentVariables((_localOptions.BarcodeScriptPath ?? string.Empty).Trim());
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

        candidates.Add(Path.Combine(_hostEnvironment.ContentRootPath, "scripts", "barcode_scan.py"));

        return candidates.FirstOrDefault(File.Exists) ?? string.Empty;
    }

    private static IReadOnlyList<string> ParseBarcodeOutput(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return [];
        }

        var codes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            if (!doc.RootElement.TryGetProperty("results", out var rows) || rows.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            foreach (var row in rows.EnumerateArray())
            {
                if (!row.TryGetProperty("codes", out var rowCodes) || rowCodes.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var codeRow in rowCodes.EnumerateArray())
                {
                    if (!codeRow.TryGetProperty("text", out var textElement))
                    {
                        continue;
                    }

                    var rawCode = NormalizeCode(textElement.GetString() ?? string.Empty);
                    var type = codeRow.TryGetProperty("type", out var typeElement)
                        ? typeElement.GetString() ?? string.Empty
                        : string.Empty;
                    if (!TryNormalizeRetailBarcode(rawCode, type, out var normalized))
                    {
                        continue;
                    }

                    var hits = ParseHitCount(codeRow);
                    if (codes.TryGetValue(normalized, out var existingHits))
                    {
                        codes[normalized] = Math.Max(existingHits, hits);
                    }
                    else
                    {
                        codes[normalized] = hits;
                    }
                }
            }
        }
        catch
        {
            // Ignore malformed barcode output.
        }

        return codes
            .OrderByDescending(kvp => kvp.Value)
            .ThenByDescending(kvp => kvp.Key.Length is 12 or 13 ? 2 : kvp.Key.Length is 8 ? 1 : 0)
            .ThenBy(kvp => kvp.Key, StringComparer.Ordinal)
            .Take(4)
            .Select(kvp => kvp.Key)
            .ToArray();
    }

    private static string ExtractScannerWarning(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return string.Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            if (doc.RootElement.TryGetProperty("warning", out var warningElement) &&
                warningElement.ValueKind == JsonValueKind.String)
            {
                return warningElement.GetString() ?? string.Empty;
            }
        }
        catch
        {
            // Ignore malformed barcode scanner output warnings.
        }

        return string.Empty;
    }

    private static int ParseHitCount(JsonElement codeRow)
    {
        if (!codeRow.TryGetProperty("hits", out var hitsElement))
        {
            return 1;
        }

        if (hitsElement.ValueKind == JsonValueKind.Number && hitsElement.TryGetInt32(out var numericHits))
        {
            return Math.Max(1, numericHits);
        }

        if (hitsElement.ValueKind == JsonValueKind.String &&
            int.TryParse(hitsElement.GetString(), out numericHits))
        {
            return Math.Max(1, numericHits);
        }

        return 1;
    }

    private static bool TryNormalizeRetailBarcode(string code, string codeType, out string normalizedCode)
    {
        normalizedCode = string.Empty;
        if (string.IsNullOrWhiteSpace(code) || !code.All(char.IsDigit))
        {
            return false;
        }

        var normalizedType = NormalizeBarcodeType(codeType);
        if (normalizedType == "EAN_13")
        {
            if (code.Length == 13 && IsEanChecksumValid(code))
            {
                normalizedCode = code;
                return true;
            }

            if (code.Length == 12 && IsUpcAChecksumValid(code))
            {
                normalizedCode = code;
                return true;
            }

            return false;
        }

        if (normalizedType == "EAN_8")
        {
            // EAN-8 is too noisy for game-cover scans; ignore unless UPC/EAN13 can be derived.
            return false;
        }

        if (normalizedType == "UPC_A")
        {
            if (code.Length == 12 && IsUpcAChecksumValid(code))
            {
                normalizedCode = code;
                return true;
            }

            if (code.Length == 13 && code.StartsWith('0') && IsEanChecksumValid(code))
            {
                normalizedCode = code[1..];
                return true;
            }

            return false;
        }

        if (normalizedType == "UPC_E")
        {
            if (TryExpandUpceToUpca(code, out var upca))
            {
                normalizedCode = upca;
                return true;
            }

            return false;
        }

        switch (code.Length)
        {
            case 13 when IsEanChecksumValid(code):
                normalizedCode = code;
                return true;
            case 12 when IsUpcAChecksumValid(code):
                normalizedCode = code;
                return true;
            case 8 when TryExpandUpceToUpca(code, out var upca):
                normalizedCode = upca;
                return true;
            default:
                return false;
        }
    }

    private static string NormalizeBarcodeType(string codeType)
    {
        var value = (codeType ?? string.Empty)
            .Trim()
            .ToUpperInvariant()
            .Replace('-', '_');

        return value switch
        {
            "EAN13" or "ISBN13" or "JAN" => "EAN_13",
            "EAN8" => "EAN_8",
            "UPCA" => "UPC_A",
            "UPCE" => "UPC_E",
            _ => value
        };
    }

    private static IReadOnlyList<string> ExpandBarcodeVariants(IReadOnlyList<string> detectedCodes)
    {
        if (detectedCodes.Count == 0)
        {
            return [];
        }

        var expanded = new List<string>(detectedCodes.Count * 2);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static void AddCode(List<string> list, HashSet<string> set, string value)
        {
            if (string.IsNullOrWhiteSpace(value) || !set.Add(value))
            {
                return;
            }

            list.Add(value);
        }

        foreach (var raw in detectedCodes)
        {
            var code = NormalizeCode(raw);
            if (string.IsNullOrWhiteSpace(code) || !code.All(char.IsDigit))
            {
                continue;
            }

            AddCode(expanded, seen, code);

            if (code.Length == 12 && IsUpcAChecksumValid(code))
            {
                var ean13 = $"0{code}";
                if (IsEanChecksumValid(ean13))
                {
                    AddCode(expanded, seen, ean13);
                }
            }
            else if (code.Length == 13 && code.StartsWith('0') && IsEanChecksumValid(code))
            {
                var upca = code[1..];
                if (IsUpcAChecksumValid(upca))
                {
                    AddCode(expanded, seen, upca);
                }
            }
            else if (code.Length == 8 && TryExpandUpceToUpca(code, out var upceUpca))
            {
                AddCode(expanded, seen, upceUpca);
                var ean13 = $"0{upceUpca}";
                if (IsEanChecksumValid(ean13))
                {
                    AddCode(expanded, seen, ean13);
                }
            }
        }

        return expanded;
    }

    private static bool TryExpandUpceToUpca(string upce, out string upca)
    {
        upca = string.Empty;
        if (string.IsNullOrWhiteSpace(upce) || upce.Length != 8 || !upce.All(char.IsDigit))
        {
            return false;
        }

        var ns = upce[0];
        var d1 = upce[1];
        var d2 = upce[2];
        var d3 = upce[3];
        var d4 = upce[4];
        var d5 = upce[5];
        var d6 = upce[6];
        var check = upce[7];

        string body = d6 switch
        {
            '0' or '1' or '2' => $"{ns}{d1}{d2}{d6}0000{d3}{d4}{d5}",
            '3' => $"{ns}{d1}{d2}{d3}00000{d4}{d5}",
            '4' => $"{ns}{d1}{d2}{d3}{d4}00000{d5}",
            _ => $"{ns}{d1}{d2}{d3}{d4}{d5}0000{d6}"
        };

        var candidate = $"{body}{check}";
        if (!IsUpcAChecksumValid(candidate))
        {
            return false;
        }

        upca = candidate;
        return true;
    }

    private static bool IsEanChecksumValid(string code)
    {
        if (!code.All(char.IsDigit) || (code.Length != 8 && code.Length != 13))
        {
            return false;
        }

        var expected = code[^1] - '0';
        var sum = 0;
        var bodyLength = code.Length - 1;
        for (var i = 0; i < bodyLength; i++)
        {
            var digit = code[i] - '0';
            if (code.Length == 13)
            {
                sum += i % 2 == 0 ? digit : digit * 3;
            }
            else
            {
                sum += i % 2 == 0 ? digit * 3 : digit;
            }
        }

        var check = (10 - (sum % 10)) % 10;
        return check == expected;
    }

    private static bool IsUpcAChecksumValid(string code)
    {
        if (!code.All(char.IsDigit) || code.Length != 12)
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

    private static string NormalizeCode(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return new string(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
