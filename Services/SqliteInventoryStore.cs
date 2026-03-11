using System.Globalization;
using System.Text.Json;
using CatalogPilot.Models;
using CatalogPilot.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace CatalogPilot.Services;

public sealed class SqliteInventoryStore : IInventoryStore
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _initialized;

    public SqliteInventoryStore(
        IWebHostEnvironment hostEnvironment,
        IOptions<InventoryStoreOptions> options)
    {
        var configuredPath = (options.Value.DatabasePath ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            configuredPath = "Data/inventory-store.db";
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
                PRAGMA foreign_keys = ON;

                CREATE TABLE IF NOT EXISTS inventory_items (
                    id TEXT NOT NULL PRIMARY KEY,
                    user_id TEXT NOT NULL,
                    status TEXT NOT NULL,
                    item_name TEXT NOT NULL,
                    description TEXT NOT NULL DEFAULT '',
                    platform TEXT NOT NULL DEFAULT '',
                    condition TEXT NOT NULL DEFAULT 'Used',
                    is_sealed INTEGER NOT NULL DEFAULT 0,
                    quantity INTEGER NOT NULL DEFAULT 1,
                    user_price_override REAL NULL,
                    photos_json TEXT NOT NULL DEFAULT '[]',
                    suggested_classification_json TEXT NULL,
                    suggested_pricing_json TEXT NULL,
                    manual_specifics_json TEXT NOT NULL DEFAULT '{}',
                    listing_id TEXT NULL,
                    listing_message TEXT NOT NULL DEFAULT '',
                    created_utc TEXT NOT NULL,
                    updated_utc TEXT NOT NULL,
                    listed_utc TEXT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_inventory_items_user_status_created
                ON inventory_items(user_id, status, created_utc);

                CREATE INDEX IF NOT EXISTS ix_inventory_items_user_created
                ON inventory_items(user_id, created_utc DESC);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<InventoryItemRecord> AddItemAsync(
        Guid userId,
        ListingInput input,
        ClassificationResult? suggestedClassification,
        PriceSuggestionResult? suggestedPricing,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        var record = new InventoryItemRecord
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Status = InventoryItemStatuses.Inactive,
            Input = CloneListingInput(input),
            SuggestedClassification = suggestedClassification,
            SuggestedPricing = suggestedPricing,
            ManualSpecifics = [],
            CreatedUtc = DateTimeOffset.UtcNow,
            UpdatedUtc = DateTimeOffset.UtcNow,
            ListingMessage = string.Empty
        };

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO inventory_items (
                    id,
                    user_id,
                    status,
                    item_name,
                    description,
                    platform,
                    condition,
                    is_sealed,
                    quantity,
                    user_price_override,
                    photos_json,
                    suggested_classification_json,
                    suggested_pricing_json,
                    manual_specifics_json,
                    listing_id,
                    listing_message,
                    created_utc,
                    updated_utc,
                    listed_utc
                ) VALUES (
                    $id,
                    $userId,
                    $status,
                    $itemName,
                    $description,
                    $platform,
                    $condition,
                    $isSealed,
                    $quantity,
                    $userPriceOverride,
                    $photosJson,
                    $suggestedClassificationJson,
                    $suggestedPricingJson,
                    $manualSpecificsJson,
                    $listingId,
                    $listingMessage,
                    $createdUtc,
                    $updatedUtc,
                    $listedUtc
                );
                """;
            BindRecordParameters(command, record);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }

        return record;
    }

    public async Task<IReadOnlyList<InventoryItemRecord>> GetItemsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await ReadItemsAsync(
            userId,
            statusFilter: null,
            cancellationToken);
    }

    public async Task<IReadOnlyList<InventoryItemRecord>> GetInactiveItemsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await ReadItemsAsync(
            userId,
            InventoryItemStatuses.Inactive,
            cancellationToken);
    }

    public async Task<InventoryItemRecord?> GetItemAsync(
        Guid userId,
        Guid itemId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                id,
                user_id,
                status,
                item_name,
                description,
                platform,
                condition,
                is_sealed,
                quantity,
                user_price_override,
                photos_json,
                suggested_classification_json,
                suggested_pricing_json,
                manual_specifics_json,
                listing_id,
                listing_message,
                created_utc,
                updated_utc,
                listed_utc
            FROM inventory_items
            WHERE user_id = $userId
              AND id = $id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$userId", userId.ToString("D"));
        command.Parameters.AddWithValue("$id", itemId.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRecord(reader) : null;
    }

    public async Task<bool> UpdateManualAttributesAsync(
        Guid userId,
        Guid itemId,
        ListingInput input,
        IReadOnlyDictionary<string, string> manualSpecifics,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var cleanSpecifics = manualSpecifics
            .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key) && !string.IsNullOrWhiteSpace(kvp.Value))
            .ToDictionary(
                kvp => kvp.Key.Trim(),
                kvp => kvp.Value.Trim(),
                StringComparer.OrdinalIgnoreCase);

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE inventory_items
                SET item_name = $itemName,
                    description = $description,
                    platform = $platform,
                    condition = $condition,
                    is_sealed = $isSealed,
                    quantity = $quantity,
                    user_price_override = $userPriceOverride,
                    photos_json = $photosJson,
                    manual_specifics_json = $manualSpecificsJson,
                    updated_utc = $updatedUtc
                WHERE user_id = $userId
                  AND id = $id;
                """;
            command.Parameters.AddWithValue("$itemName", NormalizeName(input.ItemName));
            command.Parameters.AddWithValue("$description", (input.Description ?? string.Empty).Trim());
            command.Parameters.AddWithValue("$platform", (input.Platform ?? string.Empty).Trim());
            command.Parameters.AddWithValue("$condition", NormalizeCondition(input.Condition));
            command.Parameters.AddWithValue("$isSealed", input.IsSealed ? 1 : 0);
            command.Parameters.AddWithValue("$quantity", Math.Clamp(input.Quantity, 1, 20));
            command.Parameters.AddWithValue("$userPriceOverride", input.UserPriceOverride is null ? DBNull.Value : input.UserPriceOverride.Value);
            command.Parameters.AddWithValue("$photosJson", Serialize(ClonePhotos(input.Photos)));
            command.Parameters.AddWithValue("$manualSpecificsJson", Serialize(cleanSpecifics));
            command.Parameters.AddWithValue("$updatedUtc", now.ToString("O"));
            command.Parameters.AddWithValue("$userId", userId.ToString("D"));
            command.Parameters.AddWithValue("$id", itemId.ToString("D"));

            var updated = await command.ExecuteNonQueryAsync(cancellationToken);
            return updated > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> RecordListingAttemptAsync(
        Guid userId,
        Guid itemId,
        PublishListingResult result,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var nextStatus = result.Success ? InventoryItemStatuses.Listed : InventoryItemStatuses.Inactive;

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE inventory_items
                SET status = $status,
                    listing_id = $listingId,
                    listing_message = $listingMessage,
                    listed_utc = $listedUtc,
                    updated_utc = $updatedUtc
                WHERE user_id = $userId
                  AND id = $id;
                """;
            command.Parameters.AddWithValue("$status", nextStatus);
            command.Parameters.AddWithValue("$listingId", string.IsNullOrWhiteSpace(result.ListingId) ? DBNull.Value : result.ListingId.Trim());
            command.Parameters.AddWithValue("$listingMessage", (result.Message ?? string.Empty).Trim());
            command.Parameters.AddWithValue("$listedUtc", result.Success ? now.ToString("O") : DBNull.Value);
            command.Parameters.AddWithValue("$updatedUtc", now.ToString("O"));
            command.Parameters.AddWithValue("$userId", userId.ToString("D"));
            command.Parameters.AddWithValue("$id", itemId.ToString("D"));

            var updated = await command.ExecuteNonQueryAsync(cancellationToken);
            return updated > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task<IReadOnlyList<InventoryItemRecord>> ReadItemsAsync(
        Guid userId,
        string? statusFilter,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        if (string.IsNullOrWhiteSpace(statusFilter))
        {
            command.CommandText = """
                SELECT
                    id,
                    user_id,
                    status,
                    item_name,
                    description,
                    platform,
                    condition,
                    is_sealed,
                    quantity,
                    user_price_override,
                    photos_json,
                    suggested_classification_json,
                    suggested_pricing_json,
                    manual_specifics_json,
                    listing_id,
                    listing_message,
                    created_utc,
                    updated_utc,
                    listed_utc
                FROM inventory_items
                WHERE user_id = $userId
                ORDER BY created_utc DESC;
                """;
            command.Parameters.AddWithValue("$userId", userId.ToString("D"));
        }
        else
        {
            command.CommandText = """
                SELECT
                    id,
                    user_id,
                    status,
                    item_name,
                    description,
                    platform,
                    condition,
                    is_sealed,
                    quantity,
                    user_price_override,
                    photos_json,
                    suggested_classification_json,
                    suggested_pricing_json,
                    manual_specifics_json,
                    listing_id,
                    listing_message,
                    created_utc,
                    updated_utc,
                    listed_utc
                FROM inventory_items
                WHERE user_id = $userId
                  AND status = $status
                ORDER BY created_utc ASC;
                """;
            command.Parameters.AddWithValue("$userId", userId.ToString("D"));
            command.Parameters.AddWithValue("$status", statusFilter.Trim());
        }

        var records = new List<InventoryItemRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(ReadRecord(reader));
        }

        return records;
    }

    private static void BindRecordParameters(SqliteCommand command, InventoryItemRecord record)
    {
        command.Parameters.AddWithValue("$id", record.Id.ToString("D"));
        command.Parameters.AddWithValue("$userId", record.UserId.ToString("D"));
        command.Parameters.AddWithValue("$status", (record.Status ?? InventoryItemStatuses.Inactive).Trim());
        command.Parameters.AddWithValue("$itemName", NormalizeName(record.Input.ItemName));
        command.Parameters.AddWithValue("$description", (record.Input.Description ?? string.Empty).Trim());
        command.Parameters.AddWithValue("$platform", (record.Input.Platform ?? string.Empty).Trim());
        command.Parameters.AddWithValue("$condition", NormalizeCondition(record.Input.Condition));
        command.Parameters.AddWithValue("$isSealed", record.Input.IsSealed ? 1 : 0);
        command.Parameters.AddWithValue("$quantity", Math.Clamp(record.Input.Quantity, 1, 20));
        command.Parameters.AddWithValue("$userPriceOverride", record.Input.UserPriceOverride is null ? DBNull.Value : record.Input.UserPriceOverride.Value);
        command.Parameters.AddWithValue("$photosJson", Serialize(ClonePhotos(record.Input.Photos)));
        command.Parameters.AddWithValue("$suggestedClassificationJson", record.SuggestedClassification is null ? DBNull.Value : Serialize(record.SuggestedClassification));
        command.Parameters.AddWithValue("$suggestedPricingJson", record.SuggestedPricing is null ? DBNull.Value : Serialize(record.SuggestedPricing));
        command.Parameters.AddWithValue("$manualSpecificsJson", Serialize(record.ManualSpecifics ?? new Dictionary<string, string>()));
        command.Parameters.AddWithValue("$listingId", string.IsNullOrWhiteSpace(record.ListingId) ? DBNull.Value : record.ListingId.Trim());
        command.Parameters.AddWithValue("$listingMessage", (record.ListingMessage ?? string.Empty).Trim());
        command.Parameters.AddWithValue("$createdUtc", record.CreatedUtc.ToString("O"));
        command.Parameters.AddWithValue("$updatedUtc", record.UpdatedUtc.ToString("O"));
        command.Parameters.AddWithValue("$listedUtc", record.ListedUtc is null ? DBNull.Value : record.ListedUtc.Value.ToString("O"));
    }

    private static InventoryItemRecord ReadRecord(SqliteDataReader reader)
    {
        var input = new ListingInput
        {
            ItemName = reader.GetString(3),
            Description = reader.GetString(4),
            Platform = reader.GetString(5),
            Condition = reader.GetString(6),
            IsSealed = reader.GetInt32(7) == 1,
            Quantity = reader.GetInt32(8),
            UserPriceOverride = reader.IsDBNull(9) ? null : Convert.ToDecimal(reader.GetValue(9), CultureInfo.InvariantCulture),
            Photos = Deserialize<List<UploadedPhoto>>(reader.GetString(10)) ?? []
        };

        return new InventoryItemRecord
        {
            Id = Guid.Parse(reader.GetString(0)),
            UserId = Guid.Parse(reader.GetString(1)),
            Status = reader.GetString(2),
            Input = input,
            SuggestedClassification = reader.IsDBNull(11) ? null : Deserialize<ClassificationResult>(reader.GetString(11)),
            SuggestedPricing = reader.IsDBNull(12) ? null : Deserialize<PriceSuggestionResult>(reader.GetString(12)),
            ManualSpecifics = reader.IsDBNull(13)
                ? []
                : (Deserialize<Dictionary<string, string>>(reader.GetString(13)) ?? []),
            ListingId = reader.IsDBNull(14) ? null : reader.GetString(14),
            ListingMessage = reader.IsDBNull(15) ? string.Empty : reader.GetString(15),
            CreatedUtc = ParseDateTimeOffset(reader.GetString(16)),
            UpdatedUtc = ParseDateTimeOffset(reader.GetString(17)),
            ListedUtc = reader.IsDBNull(18) ? null : ParseDateTimeOffset(reader.GetString(18))
        };
    }

    private static ListingInput CloneListingInput(ListingInput source)
    {
        return new ListingInput
        {
            ItemName = NormalizeName(source.ItemName),
            Description = (source.Description ?? string.Empty).Trim(),
            Platform = (source.Platform ?? string.Empty).Trim(),
            Condition = NormalizeCondition(source.Condition),
            IsSealed = source.IsSealed,
            Quantity = Math.Clamp(source.Quantity, 1, 20),
            UserPriceOverride = source.UserPriceOverride,
            Photos = ClonePhotos(source.Photos)
        };
    }

    private static List<UploadedPhoto> ClonePhotos(List<UploadedPhoto>? photos)
    {
        if (photos is null || photos.Count == 0)
        {
            return [];
        }

        return photos.Select(photo => new UploadedPhoto
        {
            FileName = photo.FileName,
            RelativeUrl = photo.RelativeUrl,
            ContentType = photo.ContentType,
            SizeBytes = photo.SizeBytes
        }).ToList();
    }

    private static string NormalizeName(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return trimmed;
    }

    private static string NormalizeCondition(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? "Used" : trimmed;
    }

    private static DateTimeOffset ParseDateTimeOffset(string value)
    {
        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    private static string Serialize<T>(T value)
    {
        return JsonSerializer.Serialize(value, JsonOptions);
    }

    private static T? Deserialize<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch
        {
            return default;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
