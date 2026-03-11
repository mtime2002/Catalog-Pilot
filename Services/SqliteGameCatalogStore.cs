using System.Text.Json;
using Microsoft.Data.Sqlite;
using CatalogPilot.Models;
using CatalogPilot.Options;
using Microsoft.Extensions.Options;

namespace CatalogPilot.Services;

public sealed class SqliteGameCatalogStore : IGameCatalogStore
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteGameCatalogStore> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _initialized;

    public SqliteGameCatalogStore(
        IWebHostEnvironment hostEnvironment,
        IOptions<GameCatalogStoreOptions> options,
        ILogger<SqliteGameCatalogStore> logger)
    {
        _logger = logger;
        var configuredPath = (options.Value.DatabasePath ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            configuredPath = "Data/game-catalog.db";
        }

        var dbPath = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(hostEnvironment.ContentRootPath, configuredPath);
        var dbDirectory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrWhiteSpace(dbDirectory))
        {
            Directory.CreateDirectory(dbDirectory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS game_titles (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    title TEXT NOT NULL,
                    normalized_title TEXT NOT NULL,
                    platform TEXT NOT NULL DEFAULT '',
                    normalized_platform TEXT NOT NULL DEFAULT '',
                    franchise TEXT NOT NULL DEFAULT '',
                    aliases_json TEXT NOT NULL DEFAULT '[]',
                    source TEXT NOT NULL DEFAULT '',
                    updated_utc TEXT NOT NULL
                );

                CREATE UNIQUE INDEX IF NOT EXISTS ux_game_titles_normalized
                ON game_titles(normalized_title, normalized_platform);

                CREATE INDEX IF NOT EXISTS ix_game_titles_platform
                ON game_titles(normalized_platform);

                CREATE TABLE IF NOT EXISTS game_barcodes (
                    code TEXT NOT NULL,
                    title_id INTEGER NOT NULL,
                    source TEXT NOT NULL DEFAULT '',
                    confidence REAL NOT NULL DEFAULT 0,
                    updated_utc TEXT NOT NULL,
                    PRIMARY KEY (code, title_id),
                    FOREIGN KEY (title_id) REFERENCES game_titles(id) ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS ix_game_barcodes_code
                ON game_barcodes(code);

                CREATE TABLE IF NOT EXISTS curated_titles (
                    title_id INTEGER NOT NULL PRIMARY KEY,
                    sellability_score REAL NOT NULL DEFAULT 0,
                    market_signals INTEGER NOT NULL DEFAULT 0,
                    platform_rank INTEGER NOT NULL DEFAULT 0,
                    updated_utc TEXT NOT NULL,
                    FOREIGN KEY (title_id) REFERENCES game_titles(id) ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS ix_curated_titles_rank
                ON curated_titles(platform_rank);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<int> CountTitlesAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM game_titles;";
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(value);
    }

    public async Task<int> CountCuratedTitlesAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM curated_titles;";
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(value);
    }

    public async Task<CuratedCatalogRefreshResult> RebuildCuratedCatalogAsync(
        int maxPerPlatform = 2000,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        var startedUtc = DateTimeOffset.UtcNow;
        var appliedMaxPerPlatform = Math.Clamp(maxPerPlatform, 100, 5000);

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

            var normalizedPlatforms = PhysicalConsolePlatforms
                .Select(NormalizeText)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var platformInClause = string.Join(", ", normalizedPlatforms.Select((_, index) => $"$p{index}"));

            var physicalCandidates = await ExecuteScalarIntAsync(
                connection,
                transaction,
                $"""
                SELECT COUNT(*)
                FROM game_titles
                WHERE normalized_platform IN ({platformInClause});
                """,
                command =>
                {
                    for (var i = 0; i < normalizedPlatforms.Length; i++)
                    {
                        command.Parameters.AddWithValue($"$p{i}", normalizedPlatforms[i]);
                    }
                },
                cancellationToken);

            var eligibleCandidates = await ExecuteScalarIntAsync(
                connection,
                transaction,
                $"""
                SELECT COUNT(*)
                FROM game_titles
                WHERE normalized_platform IN ({platformInClause})
                  AND normalized_title <> ''
                  AND normalized_title IS NOT NULL
                  AND {BuildCuratedTitleEligibilityPredicate("normalized_title")};
                """,
                command =>
                {
                    for (var i = 0; i < normalizedPlatforms.Length; i++)
                    {
                        command.Parameters.AddWithValue($"$p{i}", normalizedPlatforms[i]);
                    }
                },
                cancellationToken);

            await using (var deleteCommand = connection.CreateCommand())
            {
                deleteCommand.Transaction = transaction;
                deleteCommand.CommandText = "DELETE FROM curated_titles;";
                await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var insertCommand = connection.CreateCommand())
            {
                insertCommand.Transaction = transaction;
                insertCommand.CommandText = $"""
                    WITH physical_titles AS (
                        SELECT
                            t.id,
                            t.title,
                            t.normalized_title,
                            t.platform,
                            t.normalized_platform,
                            t.franchise,
                            t.aliases_json,
                            CASE
                                WHEN EXISTS (SELECT 1 FROM game_barcodes b WHERE b.title_id = t.id)
                                    THEN 1
                                ELSE 0
                            END AS has_barcode
                        FROM game_titles t
                        WHERE t.normalized_platform IN ({platformInClause})
                          AND t.normalized_title <> ''
                          AND t.normalized_title IS NOT NULL
                          AND {BuildCuratedTitleEligibilityPredicate("t.normalized_title")}
                    ),
                    title_popularity AS (
                        SELECT
                            normalized_title,
                            COUNT(DISTINCT normalized_platform) AS platform_span,
                            COUNT(*) AS variant_count
                        FROM physical_titles
                        GROUP BY normalized_title
                    ),
                    scored AS (
                        SELECT
                            p.id,
                            p.normalized_platform,
                            (
                                (tp.platform_span * 14.0) +
                                (MIN(tp.variant_count, 10) * 2.0) +
                                (CASE WHEN p.franchise <> '' THEN 8.0 ELSE 0.0 END) +
                                (CASE WHEN p.aliases_json <> '[]' THEN 4.0 ELSE 0.0 END) +
                                (CASE WHEN p.has_barcode = 1 THEN 20.0 ELSE 0.0 END) +
                                (CASE WHEN LENGTH(p.normalized_title) BETWEEN 4 AND 60 THEN 2.0 ELSE 0.0 END)
                            ) AS sellability_score
                        FROM physical_titles p
                        INNER JOIN title_popularity tp
                            ON tp.normalized_title = p.normalized_title
                    ),
                    ranked AS (
                        SELECT
                            s.id,
                            s.normalized_platform,
                            s.sellability_score,
                            ROW_NUMBER() OVER (
                                PARTITION BY s.normalized_platform
                                ORDER BY s.sellability_score DESC, s.id ASC
                            ) AS platform_rank
                        FROM scored s
                    )
                    INSERT INTO curated_titles (
                        title_id,
                        sellability_score,
                        market_signals,
                        platform_rank,
                        updated_utc)
                    SELECT
                        r.id,
                        r.sellability_score,
                        CAST(ROUND(r.sellability_score) AS INTEGER),
                        r.platform_rank,
                        $updatedUtc
                    FROM ranked r
                    WHERE r.platform_rank <= $maxPerPlatform;
                    """;
                for (var i = 0; i < normalizedPlatforms.Length; i++)
                {
                    insertCommand.Parameters.AddWithValue($"$p{i}", normalizedPlatforms[i]);
                }

                insertCommand.Parameters.AddWithValue("$updatedUtc", DateTimeOffset.UtcNow.ToString("O"));
                insertCommand.Parameters.AddWithValue("$maxPerPlatform", appliedMaxPerPlatform);
                await insertCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            var curatedTitles = await ExecuteScalarIntAsync(
                connection,
                transaction,
                "SELECT COUNT(*) FROM curated_titles;",
                configure: null,
                cancellationToken: cancellationToken);
            var platformsIncluded = await ExecuteScalarIntAsync(
                connection,
                transaction,
                """
                SELECT COUNT(DISTINCT t.normalized_platform)
                FROM curated_titles c
                INNER JOIN game_titles t ON t.id = c.title_id;
                """,
                configure: null,
                cancellationToken: cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            return new CuratedCatalogRefreshResult
            {
                Success = true,
                MaxPerPlatform = appliedMaxPerPlatform,
                PhysicalCandidates = physicalCandidates,
                EligibleCandidates = eligibleCandidates,
                CuratedTitles = curatedTitles,
                PlatformsIncluded = platformsIncluded,
                StartedUtc = startedUtc,
                FinishedUtc = DateTimeOffset.UtcNow,
                Message = $"Curated catalog rebuilt with {curatedTitles} title(s) across {platformsIncluded} platform(s)."
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to rebuild curated catalog.");
            return new CuratedCatalogRefreshResult
            {
                Success = false,
                MaxPerPlatform = appliedMaxPerPlatform,
                PhysicalCandidates = 0,
                EligibleCandidates = 0,
                CuratedTitles = 0,
                PlatformsIncluded = 0,
                StartedUtc = startedUtc,
                FinishedUtc = DateTimeOffset.UtcNow,
                Message = $"Curated catalog rebuild failed: {ex.Message}"
            };
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<CuratedPlatformSummary>> GetCuratedPlatformSummaryAsync(
        int maxPlatforms = 100,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                t.platform,
                COUNT(*) AS title_count
            FROM curated_titles c
            INNER JOIN game_titles t ON t.id = c.title_id
            GROUP BY t.platform, t.normalized_platform
            ORDER BY title_count DESC, t.platform ASC
            LIMIT $maxPlatforms;
            """;
        command.Parameters.AddWithValue("$maxPlatforms", Math.Clamp(maxPlatforms, 1, 400));

        var rows = new List<CuratedPlatformSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new CuratedPlatformSummary
            {
                Platform = reader.GetString(0),
                TitleCount = reader.GetInt32(1)
            });
        }

        return rows;
    }

    public async Task UpsertTitlesAsync(IEnumerable<GameTitleBankEntry> entries, string source, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        var items = entries
            .Where(e => !string.IsNullOrWhiteSpace(e.Title))
            .ToArray();
        if (items.Length == 0)
        {
            return;
        }

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

            foreach (var entry in items)
            {
                await UpsertTitleInternalAsync(connection, transaction, entry, source, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task UpsertBarcodeAsync(
        string code,
        GameTitleBankEntry entry,
        string source,
        decimal confidence,
        CancellationToken cancellationToken = default)
    {
        var normalizedCode = NormalizeCode(code);
        if (string.IsNullOrWhiteSpace(normalizedCode) || string.IsNullOrWhiteSpace(entry.Title))
        {
            return;
        }

        await InitializeAsync(cancellationToken);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

            var titleId = await UpsertTitleInternalAsync(connection, transaction, entry, source, cancellationToken);
            await using var barcodeCommand = connection.CreateCommand();
            barcodeCommand.Transaction = transaction;
            barcodeCommand.CommandText = """
                INSERT INTO game_barcodes (code, title_id, source, confidence, updated_utc)
                VALUES ($code, $titleId, $source, $confidence, $updatedUtc)
                ON CONFLICT(code, title_id) DO UPDATE SET
                    source = excluded.source,
                    confidence = excluded.confidence,
                    updated_utc = excluded.updated_utc;
                """;
            barcodeCommand.Parameters.AddWithValue("$code", normalizedCode);
            barcodeCommand.Parameters.AddWithValue("$titleId", titleId);
            barcodeCommand.Parameters.AddWithValue("$source", source ?? string.Empty);
            barcodeCommand.Parameters.AddWithValue("$confidence", (double)confidence);
            barcodeCommand.Parameters.AddWithValue("$updatedUtc", DateTimeOffset.UtcNow.ToString("O"));
            await barcodeCommand.ExecuteNonQueryAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<CatalogBarcodeMatchResult?> FindByBarcodeAsync(
        string code,
        string? platformHint = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedCode = NormalizeCode(code);
        if (string.IsNullOrWhiteSpace(normalizedCode))
        {
            return null;
        }

        await InitializeAsync(cancellationToken);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var useCuratedOnly = await HasCuratedTitlesAsync(connection, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = useCuratedOnly
            ? """
                SELECT
                    t.title,
                    t.platform,
                    t.franchise,
                    t.aliases_json,
                    b.code,
                    b.confidence,
                    c.sellability_score
                FROM game_barcodes b
                INNER JOIN game_titles t ON t.id = b.title_id
                INNER JOIN curated_titles c ON c.title_id = t.id
                WHERE b.code = $code;
                """
            : """
                SELECT
                    t.title,
                    t.platform,
                    t.franchise,
                    t.aliases_json,
                    b.code,
                    b.confidence,
                    0 AS sellability_score
                FROM game_barcodes b
                INNER JOIN game_titles t ON t.id = b.title_id
                WHERE b.code = $code;
                """;
        command.Parameters.AddWithValue("$code", normalizedCode);

        var normalizedPlatformHint = NormalizeText(platformHint);
        CatalogBarcodeMatchResult? best = null;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var title = reader.GetString(0);
            var platform = reader.GetString(1);
            var franchise = reader.GetString(2);
            var aliases = ParseAliases(reader.GetString(3));
            var matchedCode = reader.GetString(4);
            var confidence = reader.GetDouble(5);
            var sellabilityScore = reader.GetDouble(6);

            var score = (decimal)confidence;
            if (sellabilityScore > 0)
            {
                score += decimal.Min(0.2m, (decimal)sellabilityScore / 450m);
            }

            var normalizedPlatform = NormalizeText(platform);
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
            }

            var match = new CatalogBarcodeMatchResult
            {
                Code = matchedCode,
                Match = new GameTitleMatchResult
                {
                    Entry = new GameTitleBankEntry
                    {
                        Title = title,
                        Platform = platform,
                        Franchise = franchise,
                        Aliases = aliases
                    },
                    Score = decimal.Min(1m, score)
                }
            };

            if (best is null || match.Match.Score > best.Match.Score)
            {
                best = match;
            }
        }

        return best;
    }

    public async Task<IReadOnlyList<GameTitleMatchResult>> SearchSimilarTitlesAsync(
        string query,
        string? platformHint = null,
        int maxResults = 8,
        CancellationToken cancellationToken = default)
    {
        var normalizedQuery = NormalizeText(query);
        if (string.IsNullOrWhiteSpace(normalizedQuery))
        {
            return [];
        }

        await InitializeAsync(cancellationToken);
        var tokens = normalizedQuery
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length >= 2)
            .Distinct(StringComparer.Ordinal)
            .Take(6)
            .ToArray();
        var expandedTokens = tokens
            .SelectMany(ExpandNoisyTokenVariants)
            .Distinct(StringComparer.Ordinal)
            .Take(16)
            .ToArray();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        var whereParts = new List<string> { "t.normalized_title LIKE $queryLike" };
        command.Parameters.AddWithValue("$queryLike", $"%{normalizedQuery}%");
        for (var i = 0; i < expandedTokens.Length; i++)
        {
            var name = $"$t{i}";
            whereParts.Add($"t.normalized_title LIKE {name}");
            command.Parameters.AddWithValue(name, $"%{expandedTokens[i]}%");
        }

        command.CommandText = $"""
            SELECT
                t.title,
                t.normalized_title,
                t.platform,
                t.normalized_platform,
                t.franchise,
                t.aliases_json,
                COALESCE(c.sellability_score, 0)
            FROM game_titles t
            LEFT JOIN curated_titles c ON c.title_id = t.id
            WHERE {string.Join(" OR ", whereParts)}
            LIMIT 1500;
            """;

        var normalizedPlatformHint = NormalizeText(platformHint);
        var rows = new Dictionary<string, CatalogTitleRow>(StringComparer.Ordinal);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = ReadCatalogTitleRow(reader);
            rows[$"{row.NormalizedTitle}|{row.NormalizedPlatform}"] = row;
        }

        var results = ScoreRows(rows.Values, normalizedQuery, normalizedPlatformHint);

        // Fuzzy fallback: for highly noisy OCR, exact LIKE prefilter can miss true candidates entirely.
        if (results.Count == 0 || results.Max(r => r.Score) < 0.33m)
        {
            await using var fallbackCommand = connection.CreateCommand();
            if (string.IsNullOrWhiteSpace(normalizedPlatformHint))
            {
                fallbackCommand.CommandText = """
                    SELECT
                        t.title,
                        t.normalized_title,
                        t.platform,
                        t.normalized_platform,
                        t.franchise,
                        t.aliases_json,
                        COALESCE(c.sellability_score, 0)
                    FROM game_titles t
                    LEFT JOIN curated_titles c ON c.title_id = t.id
                    LIMIT 5000;
                    """;
            }
            else
            {
                fallbackCommand.CommandText = """
                    SELECT
                        t.title,
                        t.normalized_title,
                        t.platform,
                        t.normalized_platform,
                        t.franchise,
                        t.aliases_json,
                        COALESCE(c.sellability_score, 0)
                    FROM game_titles t
                    LEFT JOIN curated_titles c ON c.title_id = t.id
                    WHERE t.normalized_platform = $platformHint
                    LIMIT 5000;
                    """;
                fallbackCommand.Parameters.AddWithValue("$platformHint", normalizedPlatformHint);
            }

            await using var fallbackReader = await fallbackCommand.ExecuteReaderAsync(cancellationToken);
            while (await fallbackReader.ReadAsync(cancellationToken))
            {
                var row = ReadCatalogTitleRow(fallbackReader);
                rows[$"{row.NormalizedTitle}|{row.NormalizedPlatform}"] = row;
            }

            results = ScoreRows(rows.Values, normalizedQuery, normalizedPlatformHint);
        }

        return results
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Entry.Title, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(maxResults, 1, 20))
            .ToArray();
    }

    private static CatalogTitleRow ReadCatalogTitleRow(SqliteDataReader reader)
    {
        return new CatalogTitleRow(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            ParseAliases(reader.GetString(5)),
            reader.GetDouble(6));
    }

    private static List<GameTitleMatchResult> ScoreRows(
        IEnumerable<CatalogTitleRow> rows,
        string normalizedQuery,
        string normalizedPlatformHint)
    {
        var results = new List<GameTitleMatchResult>();

        foreach (var row in rows)
        {
            var score = ScoreSimilarity(
                normalizedQuery,
                normalizedPlatformHint,
                row.NormalizedTitle,
                row.NormalizedPlatform,
                row.Aliases);
            if (row.SellabilityScore > 0)
            {
                // Keep curated popularity as a light tiebreaker only.
                score += decimal.Min(0.06m, (decimal)row.SellabilityScore / 1500m);
            }

            score = decimal.Min(1m, score);
            if (score < 0.16m)
            {
                continue;
            }

            results.Add(new GameTitleMatchResult
            {
                Entry = new GameTitleBankEntry
                {
                    Title = row.Title,
                    Platform = row.Platform,
                    Franchise = row.Franchise,
                    Aliases = row.Aliases
                },
                Score = score
            });
        }

        return results;
    }

    private async Task<long> UpsertTitleInternalAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        GameTitleBankEntry entry,
        string source,
        CancellationToken cancellationToken)
    {
        var title = NormalizeWhitespace(entry.Title);
        var normalizedTitle = NormalizeText(title);
        var platform = NormalizeWhitespace(entry.Platform);
        var normalizedPlatform = NormalizeText(platform);
        var franchise = NormalizeWhitespace(entry.Franchise);
        var aliases = entry.Aliases
            .Select(NormalizeWhitespace)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var aliasesJson = JsonSerializer.Serialize(aliases);
        var updatedUtc = DateTimeOffset.UtcNow.ToString("O");

        await using var upsertCommand = connection.CreateCommand();
        upsertCommand.Transaction = transaction;
        upsertCommand.CommandText = """
            INSERT INTO game_titles (
                title,
                normalized_title,
                platform,
                normalized_platform,
                franchise,
                aliases_json,
                source,
                updated_utc)
            VALUES (
                $title,
                $normalizedTitle,
                $platform,
                $normalizedPlatform,
                $franchise,
                $aliasesJson,
                $source,
                $updatedUtc)
            ON CONFLICT(normalized_title, normalized_platform) DO UPDATE SET
                title = excluded.title,
                franchise = CASE WHEN excluded.franchise <> '' THEN excluded.franchise ELSE game_titles.franchise END,
                aliases_json = CASE WHEN excluded.aliases_json <> '[]' THEN excluded.aliases_json ELSE game_titles.aliases_json END,
                source = excluded.source,
                updated_utc = excluded.updated_utc;
            """;
        upsertCommand.Parameters.AddWithValue("$title", title);
        upsertCommand.Parameters.AddWithValue("$normalizedTitle", normalizedTitle);
        upsertCommand.Parameters.AddWithValue("$platform", platform);
        upsertCommand.Parameters.AddWithValue("$normalizedPlatform", normalizedPlatform);
        upsertCommand.Parameters.AddWithValue("$franchise", franchise);
        upsertCommand.Parameters.AddWithValue("$aliasesJson", aliasesJson);
        upsertCommand.Parameters.AddWithValue("$source", source ?? string.Empty);
        upsertCommand.Parameters.AddWithValue("$updatedUtc", updatedUtc);
        await upsertCommand.ExecuteNonQueryAsync(cancellationToken);

        await using var selectIdCommand = connection.CreateCommand();
        selectIdCommand.Transaction = transaction;
        selectIdCommand.CommandText = """
            SELECT id
            FROM game_titles
            WHERE normalized_title = $normalizedTitle
              AND normalized_platform = $normalizedPlatform
            LIMIT 1;
            """;
        selectIdCommand.Parameters.AddWithValue("$normalizedTitle", normalizedTitle);
        selectIdCommand.Parameters.AddWithValue("$normalizedPlatform", normalizedPlatform);
        var idValue = await selectIdCommand.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(idValue);
    }

    private static decimal ScoreSimilarity(
        string normalizedQuery,
        string normalizedPlatformHint,
        string normalizedTitle,
        string normalizedPlatform,
        IReadOnlyList<string> aliases)
    {
        var candidates = aliases
            .Select(NormalizeText)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Append(normalizedTitle);

        decimal best = 0m;
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var ocrNormalizedQuery = NormalizeOcrLike(normalizedQuery);
            var ocrNormalizedCandidate = NormalizeOcrLike(candidate);
            var trigram = TrigramJaccard(ocrNormalizedQuery, ocrNormalizedCandidate);
            var overlap = TokenOverlap(ocrNormalizedQuery, ocrNormalizedCandidate);
            var edit = EditSimilarity(ocrNormalizedQuery, ocrNormalizedCandidate);
            var fuzzyToken = FuzzyTokenSimilarity(ocrNormalizedQuery, ocrNormalizedCandidate);
            var coverageAdjust = TokenCoverageAdjustment(ocrNormalizedQuery, ocrNormalizedCandidate);
            var editionAdjust = EditionSpecificityAdjustment(ocrNormalizedQuery, ocrNormalizedCandidate);
            var prefix = (candidate.StartsWith(normalizedQuery, StringComparison.Ordinal) ||
                          normalizedQuery.StartsWith(candidate, StringComparison.Ordinal))
                ? 0.08m
                : 0m;

            var score = (trigram * 0.33m) + (overlap * 0.2m) + (edit * 0.2m) + (fuzzyToken * 0.27m) + prefix + coverageAdjust + editionAdjust;
            best = decimal.Max(best, score);
        }

        if (!string.IsNullOrWhiteSpace(normalizedPlatformHint))
        {
            if (normalizedPlatform == normalizedPlatformHint)
            {
                best += 0.08m;
            }
            else if (!string.IsNullOrWhiteSpace(normalizedPlatform) &&
                     (normalizedPlatform.Contains(normalizedPlatformHint, StringComparison.Ordinal) ||
                      normalizedPlatformHint.Contains(normalizedPlatform, StringComparison.Ordinal)))
            {
                best += 0.04m;
            }
        }

        return decimal.Min(1m, best);
    }

    private static decimal FuzzyTokenSimilarity(string query, string candidate)
    {
        var queryTokens = query
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length >= 3)
            .Where(t => !CommonStopWordTokens.Contains(t))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (queryTokens.Length == 0)
        {
            return 0m;
        }

        var candidateTokens = candidate
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length >= 2)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (candidateTokens.Length == 0)
        {
            return 0m;
        }

        var scores = new List<decimal>(queryTokens.Length);
        foreach (var queryToken in queryTokens)
        {
            decimal tokenBest = 0m;
            foreach (var candidateToken in candidateTokens)
            {
                tokenBest = decimal.Max(tokenBest, TokenFuzzySimilarity(queryToken, candidateToken));
            }

            if (tokenBest > 0m)
            {
                scores.Add(tokenBest);
            }
        }

        if (scores.Count == 0)
        {
            return 0m;
        }

        var top = scores
            .OrderByDescending(s => s)
            .Take(Math.Min(3, scores.Count))
            .ToArray();
        return top.Average();
    }

    private static decimal TokenCoverageAdjustment(string query, string candidate)
    {
        var queryTokens = query
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length >= 3)
            .Where(t => !CommonStopWordTokens.Contains(t))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (queryTokens.Length < 2)
        {
            return 0m;
        }

        var candidateTokens = candidate
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length >= 3)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (candidateTokens.Length == 0)
        {
            return 0m;
        }

        var strongHits = 0;
        foreach (var queryToken in queryTokens)
        {
            decimal bestHit = 0m;
            foreach (var candidateToken in candidateTokens)
            {
                bestHit = decimal.Max(bestHit, TokenFuzzySimilarity(queryToken, candidateToken));
            }

            if (bestHit >= 0.86m)
            {
                strongHits++;
            }
        }

        if (strongHits >= 2)
        {
            return 0.10m + decimal.Min(0.08m, (strongHits - 2) * 0.03m);
        }

        if (queryTokens.Length >= 4 && strongHits <= 1)
        {
            // Penalize franchise-only matches for long noisy OCR queries.
            return -0.07m;
        }

        return 0m;
    }

    private static decimal TokenFuzzySimilarity(string queryToken, string candidateToken)
    {
        if (queryToken.Equals(candidateToken, StringComparison.Ordinal))
        {
            return 1m;
        }

        if (queryToken.Contains(candidateToken, StringComparison.Ordinal) ||
            candidateToken.Contains(queryToken, StringComparison.Ordinal))
        {
            return 0.88m;
        }

        var edit = EditSimilarity(queryToken, candidateToken);
        var subsequence = SubsequenceSimilarity(queryToken, candidateToken);
        return decimal.Max(edit, subsequence);
    }

    private static decimal EditionSpecificityAdjustment(string query, string candidate)
    {
        var querySignals = EditionSignals.Where(signal => query.Contains(signal, StringComparison.Ordinal)).ToArray();
        var candidateSignals = EditionSignals.Where(signal => candidate.Contains(signal, StringComparison.Ordinal)).ToArray();
        if (candidateSignals.Length == 0)
        {
            return 0m;
        }

        if (querySignals.Length == 0)
        {
            return -0.09m;
        }

        var sharedSignals = candidateSignals
            .Intersect(querySignals, StringComparer.Ordinal)
            .ToArray();
        return sharedSignals.Length > 0 ? 0.03m : -0.04m;
    }

    private static decimal SubsequenceSimilarity(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return 0m;
        }

        var lcs = LongestCommonSubsequenceLength(a, b);
        return (decimal)lcs / Math.Max(a.Length, b.Length);
    }

    private static int LongestCommonSubsequenceLength(string a, string b)
    {
        var rows = a.Length + 1;
        var cols = b.Length + 1;
        var dp = new int[rows, cols];

        for (var i = 1; i < rows; i++)
        {
            for (var j = 1; j < cols; j++)
            {
                if (a[i - 1] == b[j - 1])
                {
                    dp[i, j] = dp[i - 1, j - 1] + 1;
                }
                else
                {
                    dp[i, j] = Math.Max(dp[i - 1, j], dp[i, j - 1]);
                }
            }
        }

        return dp[a.Length, b.Length];
    }

    private static decimal TokenOverlap(string a, string b)
    {
        var left = a.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var right = b.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (left.Length == 0 || right.Length == 0)
        {
            return 0m;
        }

        var overlap = left.Count(token => right.Contains(token, StringComparer.Ordinal));
        return (decimal)overlap / decimal.Max(left.Length, right.Length);
    }

    private static IEnumerable<string> ExpandNoisyTokenVariants(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            yield break;
        }

        yield return token;

        if (token.Length < 3)
        {
            yield break;
        }

        // OCR on stylized logos often drops/warps the first glyph (for example: "tfam" vs "infam").
        if (token.Length is >= 4 and <= 10 && token.All(char.IsLetter))
        {
            yield return $"i{token}";
            yield return $"in{token}";
            yield return $"in{token[1..]}";
        }

        if (token.Length >= 4)
        {
            yield return token.Replace("rn", "m", StringComparison.Ordinal);
            yield return token.Replace("vv", "w", StringComparison.Ordinal);
        }

        foreach (var split in SplitKnownCompoundToken(token))
        {
            yield return split;
        }
    }

    private static IEnumerable<string> SplitKnownCompoundToken(string token)
    {
        if (token.Length < 9 || !token.All(char.IsLetter))
        {
            yield break;
        }

        foreach (var suffix in KnownCompoundSuffixTokens)
        {
            if (!token.EndsWith(suffix, StringComparison.Ordinal))
            {
                continue;
            }

            var prefix = token[..(token.Length - suffix.Length)];
            if (prefix.Length < 3)
            {
                continue;
            }

            yield return prefix;
            yield return suffix;
            yield break;
        }
    }

    private static decimal TrigramJaccard(string a, string b)
    {
        var left = BuildNgrams(a, 3);
        var right = BuildNgrams(b, 3);
        if (left.Count == 0 || right.Count == 0)
        {
            return 0m;
        }

        var intersection = left.Intersect(right, StringComparer.Ordinal).Count();
        var union = left.Union(right, StringComparer.Ordinal).Count();
        if (union == 0)
        {
            return 0m;
        }

        return (decimal)intersection / union;
    }

    private static HashSet<string> BuildNgrams(string value, int n)
    {
        var compact = value.Replace(" ", string.Empty, StringComparison.Ordinal);
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (compact.Length < n)
        {
            return set;
        }

        for (var i = 0; i <= compact.Length - n; i++)
        {
            set.Add(compact.Substring(i, n));
        }

        return set;
    }

    private static decimal EditSimilarity(string a, string b)
    {
        if (a == b)
        {
            return 1m;
        }

        var max = Math.Max(a.Length, b.Length);
        if (max == 0)
        {
            return 0m;
        }

        var distance = EditDistance(a, b);
        return decimal.Max(0m, 1m - ((decimal)distance / max));
    }

    private static int EditDistance(string a, string b)
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

    private static string NormalizeWhitespace(string value)
    {
        return string.Join(' ', (value ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var chars = value
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ')
            .ToArray();
        return NormalizeWhitespace(new string(chars));
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

        return NormalizeText(mapped);
    }

    private static string NormalizeCode(string? value)
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

    private static string[] ParseAliases(string aliasesJson)
    {
        if (string.IsNullOrWhiteSpace(aliasesJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(aliasesJson) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static string BuildCuratedTitleEligibilityPredicate(string normalizedTitleColumn)
    {
        var paddedTitle = $"' ' || {normalizedTitleColumn} || ' '";
        return string.Join(" AND ", CuratedExcludedTitleTerms.Select(term => $"{paddedTitle} NOT LIKE '% {term} %'"));
    }

    private static async Task<int> ExecuteScalarIntAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        Action<SqliteCommand>? configure,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        configure?.Invoke(command);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(value);
    }

    private static async Task<bool> HasCuratedTitlesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM curated_titles LIMIT 1;";
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is not null && value is not DBNull;
    }

    private sealed record CatalogTitleRow(
        string Title,
        string NormalizedTitle,
        string Platform,
        string NormalizedPlatform,
        string Franchise,
        string[] Aliases,
        double SellabilityScore);

    private static readonly string[] PhysicalConsolePlatforms =
    [
        "PlayStation",
        "PlayStation 2",
        "PlayStation 3",
        "PlayStation 4",
        "PlayStation 5",
        "PlayStation Portable",
        "PlayStation Vita",
        "Xbox",
        "Xbox 360",
        "Xbox One",
        "Xbox Series X|S",
        "Nintendo Switch",
        "Nintendo Switch 2",
        "Wii",
        "Wii U",
        "Nintendo GameCube",
        "Nintendo 64",
        "Nintendo DS",
        "Nintendo DSi",
        "Nintendo 3DS",
        "Super Nintendo Entertainment System",
        "Nintendo Entertainment System",
        "Game Boy",
        "Game Boy Color",
        "Game Boy Advance",
        "Sega Mega Drive/Genesis",
        "Sega Saturn",
        "Dreamcast",
        "Neo Geo AES",
        "Neo Geo MVS",
        "Neo Geo CD",
        "Neo Geo Pocket Color",
        "Atari 2600",
        "Atari 5200",
        "Atari 8-bit",
        "Atari Jaguar",
        "Atari Lynx",
        "Atari ST/STE"
    ];

    private static readonly string[] CuratedExcludedTitleTerms =
    [
        "dlc",
        "season pass",
        "soundtrack",
        "ost",
        "expansion",
        "beta",
        "demo",
        "prototype",
        "trial",
        "avatar",
        "theme",
        "wallpaper",
        "test build",
        "online pass"
    ];

    private static readonly HashSet<string> CommonStopWordTokens = new(StringComparer.Ordinal)
    {
        "the",
        "and",
        "for",
        "with",
        "from",
        "only",
        "playstation",
        "network",
        "rated",
        "not",
        "by",
        "sur",
        "uniquement"
    };

    private static readonly string[] KnownCompoundSuffixTokens =
    [
        "thieves",
        "deception",
        "remastered",
        "collection",
        "chronicles",
        "edition",
        "fortunes",
        "fortune",
        "horizons",
        "royal",
        "drakes",
        "drake",
        "siege"
    ];

    private static readonly string[] EditionSignals =
    [
        "game of the year",
        "goty",
        "collector",
        "collectors",
        "limited",
        "special",
        "deluxe",
        "ultimate",
        "complete",
        "definitive",
        "anniversary",
        "greatest hits",
        "platinum",
        "remaster",
        "remastered",
        "expansion",
        "pack",
        "bundle",
        "edition"
    ];
}
