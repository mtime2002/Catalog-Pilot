using System.Globalization;
using Microsoft.Data.Sqlite;
using CatalogPilot.Models;
using CatalogPilot.Options;
using Microsoft.Extensions.Options;

namespace CatalogPilot.Services;

public sealed class SqliteUserAccountStore : IUserAccountStore
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _initialized;

    public SqliteUserAccountStore(
        IWebHostEnvironment hostEnvironment,
        IOptions<AuthStoreOptions> options)
    {
        var configuredPath = (options.Value.DatabasePath ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            configuredPath = "Data/auth-store.db";
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

                CREATE TABLE IF NOT EXISTS app_users (
                    id TEXT NOT NULL PRIMARY KEY,
                    email TEXT NOT NULL,
                    normalized_email TEXT NOT NULL,
                    full_name TEXT NOT NULL DEFAULT '',
                    password_hash TEXT NOT NULL,
                    stripe_customer_id TEXT NULL,
                    created_utc TEXT NOT NULL,
                    updated_utc TEXT NOT NULL
                );

                CREATE UNIQUE INDEX IF NOT EXISTS ux_app_users_normalized_email
                ON app_users(normalized_email);

                CREATE UNIQUE INDEX IF NOT EXISTS ux_app_users_stripe_customer
                ON app_users(stripe_customer_id)
                WHERE stripe_customer_id IS NOT NULL;

                CREATE TABLE IF NOT EXISTS user_subscriptions (
                    user_id TEXT NOT NULL PRIMARY KEY,
                    plan_code TEXT NOT NULL,
                    status TEXT NOT NULL,
                    stripe_subscription_id TEXT NULL,
                    stripe_customer_id TEXT NULL,
                    current_period_end_utc TEXT NULL,
                    cancel_at_period_end INTEGER NOT NULL DEFAULT 0,
                    trial_end_utc TEXT NULL,
                    created_utc TEXT NOT NULL,
                    updated_utc TEXT NOT NULL,
                    FOREIGN KEY (user_id) REFERENCES app_users(id) ON DELETE CASCADE
                );

                CREATE UNIQUE INDEX IF NOT EXISTS ux_user_subscriptions_stripe_subscription
                ON user_subscriptions(stripe_subscription_id)
                WHERE stripe_subscription_id IS NOT NULL;

                CREATE INDEX IF NOT EXISTS ix_user_subscriptions_customer
                ON user_subscriptions(stripe_customer_id);

                CREATE TABLE IF NOT EXISTS stripe_events (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    stripe_event_id TEXT NOT NULL,
                    event_type TEXT NOT NULL,
                    payload_json TEXT NOT NULL,
                    processed_utc TEXT NOT NULL,
                    success INTEGER NOT NULL DEFAULT 0,
                    message TEXT NOT NULL DEFAULT ''
                );

                CREATE UNIQUE INDEX IF NOT EXISTS ux_stripe_events_event_id
                ON stripe_events(stripe_event_id);

                CREATE TABLE IF NOT EXISTS usage_events (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    user_id TEXT NOT NULL,
                    usage_type TEXT NOT NULL,
                    quantity INTEGER NOT NULL DEFAULT 1,
                    occurred_utc TEXT NOT NULL,
                    FOREIGN KEY (user_id) REFERENCES app_users(id) ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS ix_usage_events_user_type_period
                ON usage_events(user_id, usage_type, occurred_utc);
                """;

            await command.ExecuteNonQueryAsync(cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<AppUserRecord?> GetByIdAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, email, full_name, password_hash, stripe_customer_id, created_utc, updated_utc
            FROM app_users
            WHERE id = $id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", userId.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadUser(reader) : null;
    }

    public async Task<AppUserRecord?> GetByEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        var normalized = NormalizeEmail(email);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, email, full_name, password_hash, stripe_customer_id, created_utc, updated_utc
            FROM app_users
            WHERE normalized_email = $normalizedEmail
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$normalizedEmail", normalized);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadUser(reader) : null;
    }

    public async Task<AppUserRecord?> GetByStripeCustomerIdAsync(string stripeCustomerId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(stripeCustomerId))
        {
            return null;
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, email, full_name, password_hash, stripe_customer_id, created_utc, updated_utc
            FROM app_users
            WHERE stripe_customer_id = $stripeCustomerId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$stripeCustomerId", stripeCustomerId.Trim());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadUser(reader) : null;
    }

    public async Task<(bool Success, string ErrorMessage, AppUserRecord? User)> CreateUserAsync(
        string email,
        string fullName,
        string passwordHash,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        var normalizedEmail = NormalizeEmail(email);
        if (string.IsNullOrWhiteSpace(normalizedEmail))
        {
            return (false, "Email is required.", null);
        }

        var now = DateTimeOffset.UtcNow;
        var userId = Guid.NewGuid();
        var emailToPersist = email.Trim();
        var nameToPersist = string.IsNullOrWhiteSpace(fullName) ? emailToPersist : fullName.Trim();

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

            try
            {
                await using (var insertUserCommand = connection.CreateCommand())
                {
                    insertUserCommand.Transaction = transaction;
                    insertUserCommand.CommandText = """
                        INSERT INTO app_users (
                            id,
                            email,
                            normalized_email,
                            full_name,
                            password_hash,
                            created_utc,
                            updated_utc
                        ) VALUES (
                            $id,
                            $email,
                            $normalizedEmail,
                            $fullName,
                            $passwordHash,
                            $createdUtc,
                            $updatedUtc
                        );
                        """;
                    insertUserCommand.Parameters.AddWithValue("$id", userId.ToString("D"));
                    insertUserCommand.Parameters.AddWithValue("$email", emailToPersist);
                    insertUserCommand.Parameters.AddWithValue("$normalizedEmail", normalizedEmail);
                    insertUserCommand.Parameters.AddWithValue("$fullName", nameToPersist);
                    insertUserCommand.Parameters.AddWithValue("$passwordHash", passwordHash);
                    insertUserCommand.Parameters.AddWithValue("$createdUtc", now.ToString("O"));
                    insertUserCommand.Parameters.AddWithValue("$updatedUtc", now.ToString("O"));
                    await insertUserCommand.ExecuteNonQueryAsync(cancellationToken);
                }

                await using (var insertSubscriptionCommand = connection.CreateCommand())
                {
                    insertSubscriptionCommand.Transaction = transaction;
                    insertSubscriptionCommand.CommandText = """
                        INSERT INTO user_subscriptions (
                            user_id,
                            plan_code,
                            status,
                            created_utc,
                            updated_utc
                        ) VALUES (
                            $userId,
                            'free',
                            'inactive',
                            $createdUtc,
                            $updatedUtc
                        );
                        """;
                    insertSubscriptionCommand.Parameters.AddWithValue("$userId", userId.ToString("D"));
                    insertSubscriptionCommand.Parameters.AddWithValue("$createdUtc", now.ToString("O"));
                    insertSubscriptionCommand.Parameters.AddWithValue("$updatedUtc", now.ToString("O"));
                    await insertSubscriptionCommand.ExecuteNonQueryAsync(cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (false, "Email is already registered.", null);
            }

            return (true, string.Empty, new AppUserRecord
            {
                Id = userId,
                Email = emailToPersist,
                FullName = nameToPersist,
                PasswordHash = passwordHash,
                CreatedUtc = now,
                UpdatedUtc = now
            });
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task UpdateStripeCustomerIdAsync(
        Guid userId,
        string stripeCustomerId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(stripeCustomerId))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

            await using (var updateUserCommand = connection.CreateCommand())
            {
                updateUserCommand.Transaction = transaction;
                updateUserCommand.CommandText = """
                    UPDATE app_users
                    SET stripe_customer_id = $stripeCustomerId,
                        updated_utc = $updatedUtc
                    WHERE id = $userId;
                    """;
                updateUserCommand.Parameters.AddWithValue("$stripeCustomerId", stripeCustomerId.Trim());
                updateUserCommand.Parameters.AddWithValue("$updatedUtc", now.ToString("O"));
                updateUserCommand.Parameters.AddWithValue("$userId", userId.ToString("D"));
                await updateUserCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var upsertSubscriptionCommand = connection.CreateCommand())
            {
                upsertSubscriptionCommand.Transaction = transaction;
                upsertSubscriptionCommand.CommandText = """
                    INSERT INTO user_subscriptions (
                        user_id,
                        plan_code,
                        status,
                        stripe_customer_id,
                        created_utc,
                        updated_utc
                    ) VALUES (
                        $userId,
                        'free',
                        'inactive',
                        $stripeCustomerId,
                        $createdUtc,
                        $updatedUtc
                    )
                    ON CONFLICT(user_id) DO UPDATE SET
                        stripe_customer_id = excluded.stripe_customer_id,
                        updated_utc = excluded.updated_utc;
                    """;
                upsertSubscriptionCommand.Parameters.AddWithValue("$userId", userId.ToString("D"));
                upsertSubscriptionCommand.Parameters.AddWithValue("$stripeCustomerId", stripeCustomerId.Trim());
                upsertSubscriptionCommand.Parameters.AddWithValue("$createdUtc", now.ToString("O"));
                upsertSubscriptionCommand.Parameters.AddWithValue("$updatedUtc", now.ToString("O"));
                await upsertSubscriptionCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<UserSubscriptionRecord> GetOrCreateSubscriptionAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using (var existingCommand = connection.CreateCommand())
        {
            existingCommand.CommandText = """
                SELECT
                    user_id,
                    plan_code,
                    status,
                    stripe_subscription_id,
                    stripe_customer_id,
                    current_period_end_utc,
                    cancel_at_period_end,
                    trial_end_utc,
                    created_utc,
                    updated_utc
                FROM user_subscriptions
                WHERE user_id = $userId
                LIMIT 1;
                """;
            existingCommand.Parameters.AddWithValue("$userId", userId.ToString("D"));
            await using var reader = await existingCommand.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                return ReadSubscription(reader);
            }
        }

        var created = new UserSubscriptionRecord
        {
            UserId = userId,
            PlanCode = "free",
            Status = "inactive",
            CreatedUtc = DateTimeOffset.UtcNow,
            UpdatedUtc = DateTimeOffset.UtcNow
        };
        await UpsertSubscriptionAsync(created, cancellationToken);
        return created;
    }

    public async Task UpsertSubscriptionAsync(
        UserSubscriptionRecord subscription,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var createdUtc = subscription.CreatedUtc == default ? now : subscription.CreatedUtc;
        var updatedUtc = now;

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO user_subscriptions (
                    user_id,
                    plan_code,
                    status,
                    stripe_subscription_id,
                    stripe_customer_id,
                    current_period_end_utc,
                    cancel_at_period_end,
                    trial_end_utc,
                    created_utc,
                    updated_utc
                ) VALUES (
                    $userId,
                    $planCode,
                    $status,
                    $stripeSubscriptionId,
                    $stripeCustomerId,
                    $currentPeriodEndUtc,
                    $cancelAtPeriodEnd,
                    $trialEndUtc,
                    $createdUtc,
                    $updatedUtc
                )
                ON CONFLICT(user_id) DO UPDATE SET
                    plan_code = excluded.plan_code,
                    status = excluded.status,
                    stripe_subscription_id = excluded.stripe_subscription_id,
                    stripe_customer_id = excluded.stripe_customer_id,
                    current_period_end_utc = excluded.current_period_end_utc,
                    cancel_at_period_end = excluded.cancel_at_period_end,
                    trial_end_utc = excluded.trial_end_utc,
                    updated_utc = excluded.updated_utc;
                """;
            command.Parameters.AddWithValue("$userId", subscription.UserId.ToString("D"));
            command.Parameters.AddWithValue("$planCode", subscription.PlanCode.Trim());
            command.Parameters.AddWithValue("$status", subscription.Status.Trim());
            command.Parameters.AddWithValue("$stripeSubscriptionId", (object?)subscription.StripeSubscriptionId ?? DBNull.Value);
            command.Parameters.AddWithValue("$stripeCustomerId", (object?)subscription.StripeCustomerId ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "$currentPeriodEndUtc",
                subscription.CurrentPeriodEndUtc is null ? DBNull.Value : subscription.CurrentPeriodEndUtc.Value.ToString("O"));
            command.Parameters.AddWithValue("$cancelAtPeriodEnd", subscription.CancelAtPeriodEnd ? 1 : 0);
            command.Parameters.AddWithValue(
                "$trialEndUtc",
                subscription.TrialEndUtc is null ? DBNull.Value : subscription.TrialEndUtc.Value.ToString("O"));
            command.Parameters.AddWithValue("$createdUtc", createdUtc.ToString("O"));
            command.Parameters.AddWithValue("$updatedUtc", updatedUtc.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> HasProcessedStripeEventAsync(string stripeEventId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(stripeEventId))
        {
            return false;
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1
            FROM stripe_events
            WHERE stripe_event_id = $stripeEventId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$stripeEventId", stripeEventId.Trim());

        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is not null;
    }

    public async Task RecordStripeEventAsync(
        string stripeEventId,
        string eventType,
        string payloadJson,
        bool success,
        string message,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(stripeEventId))
        {
            return;
        }

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO stripe_events (
                    stripe_event_id,
                    event_type,
                    payload_json,
                    processed_utc,
                    success,
                    message
                ) VALUES (
                    $stripeEventId,
                    $eventType,
                    $payloadJson,
                    $processedUtc,
                    $success,
                    $message
                );
                """;
            command.Parameters.AddWithValue("$stripeEventId", stripeEventId.Trim());
            command.Parameters.AddWithValue("$eventType", eventType.Trim());
            command.Parameters.AddWithValue("$payloadJson", payloadJson);
            command.Parameters.AddWithValue("$processedUtc", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$success", success ? 1 : 0);
            command.Parameters.AddWithValue("$message", message);

            try
            {
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                // Stripe can retry events. Ignore duplicate insert attempts.
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<int> CountUsageEventsAsync(
        Guid userId,
        string usageType,
        DateTimeOffset periodStartUtc,
        DateTimeOffset periodEndUtc,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(SUM(quantity), 0)
            FROM usage_events
            WHERE user_id = $userId
              AND usage_type = $usageType
              AND occurred_utc >= $startUtc
              AND occurred_utc < $endUtc;
            """;
        command.Parameters.AddWithValue("$userId", userId.ToString("D"));
        command.Parameters.AddWithValue("$usageType", usageType.Trim());
        command.Parameters.AddWithValue("$startUtc", periodStartUtc.ToString("O"));
        command.Parameters.AddWithValue("$endUtc", periodEndUtc.ToString("O"));
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(value);
    }

    public async Task<(bool Consumed, int UsedAfter)> TryConsumeUsageAsync(
        Guid userId,
        string usageType,
        int quantity,
        int limit,
        DateTimeOffset periodStartUtc,
        DateTimeOffset periodEndUtc,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        var requestedQuantity = Math.Max(1, quantity);
        var maxAllowed = Math.Max(1, limit);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

            var alreadyUsed = 0;
            await using (var usageCommand = connection.CreateCommand())
            {
                usageCommand.Transaction = transaction;
                usageCommand.CommandText = """
                    SELECT COALESCE(SUM(quantity), 0)
                    FROM usage_events
                    WHERE user_id = $userId
                      AND usage_type = $usageType
                      AND occurred_utc >= $startUtc
                      AND occurred_utc < $endUtc;
                    """;
                usageCommand.Parameters.AddWithValue("$userId", userId.ToString("D"));
                usageCommand.Parameters.AddWithValue("$usageType", usageType.Trim());
                usageCommand.Parameters.AddWithValue("$startUtc", periodStartUtc.ToString("O"));
                usageCommand.Parameters.AddWithValue("$endUtc", periodEndUtc.ToString("O"));
                var value = await usageCommand.ExecuteScalarAsync(cancellationToken);
                alreadyUsed = Convert.ToInt32(value);
            }

            var usedAfter = alreadyUsed;
            if (alreadyUsed + requestedQuantity <= maxAllowed)
            {
                await using var insertUsageCommand = connection.CreateCommand();
                insertUsageCommand.Transaction = transaction;
                insertUsageCommand.CommandText = """
                    INSERT INTO usage_events (
                        user_id,
                        usage_type,
                        quantity,
                        occurred_utc
                    ) VALUES (
                        $userId,
                        $usageType,
                        $quantity,
                        $occurredUtc
                    );
                    """;
                insertUsageCommand.Parameters.AddWithValue("$userId", userId.ToString("D"));
                insertUsageCommand.Parameters.AddWithValue("$usageType", usageType.Trim());
                insertUsageCommand.Parameters.AddWithValue("$quantity", requestedQuantity);
                insertUsageCommand.Parameters.AddWithValue("$occurredUtc", DateTimeOffset.UtcNow.ToString("O"));
                await insertUsageCommand.ExecuteNonQueryAsync(cancellationToken);
                usedAfter += requestedQuantity;

                await transaction.CommitAsync(cancellationToken);
                return (true, usedAfter);
            }

            await transaction.RollbackAsync(cancellationToken);
            return (false, usedAfter);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static AppUserRecord ReadUser(SqliteDataReader reader)
    {
        return new AppUserRecord
        {
            Id = Guid.Parse(reader.GetString(0)),
            Email = reader.GetString(1),
            FullName = reader.GetString(2),
            PasswordHash = reader.GetString(3),
            StripeCustomerId = reader.IsDBNull(4) ? null : reader.GetString(4),
            CreatedUtc = ParseDateTimeOffset(reader.GetString(5)),
            UpdatedUtc = ParseDateTimeOffset(reader.GetString(6))
        };
    }

    private static UserSubscriptionRecord ReadSubscription(SqliteDataReader reader)
    {
        return new UserSubscriptionRecord
        {
            UserId = Guid.Parse(reader.GetString(0)),
            PlanCode = reader.GetString(1),
            Status = reader.GetString(2),
            StripeSubscriptionId = reader.IsDBNull(3) ? null : reader.GetString(3),
            StripeCustomerId = reader.IsDBNull(4) ? null : reader.GetString(4),
            CurrentPeriodEndUtc = reader.IsDBNull(5) ? null : ParseDateTimeOffset(reader.GetString(5)),
            CancelAtPeriodEnd = reader.GetInt32(6) == 1,
            TrialEndUtc = reader.IsDBNull(7) ? null : ParseDateTimeOffset(reader.GetString(7)),
            CreatedUtc = ParseDateTimeOffset(reader.GetString(8)),
            UpdatedUtc = ParseDateTimeOffset(reader.GetString(9))
        };
    }

    private static DateTimeOffset ParseDateTimeOffset(string value)
    {
        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    private static string NormalizeEmail(string email)
    {
        return email.Trim().ToUpperInvariant();
    }
}
