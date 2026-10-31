using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Telemetry.Agent.Configuration;
using Telemetry.Agent.Security;
using Telemetry.Shared;

namespace Telemetry.Agent.Storage;

public sealed class SqliteOfflineSampleStore : IOfflineSampleStore, IAsyncDisposable
{
    private readonly AgentOptions _options;
    private readonly IDataProtectionService _dataProtection;
    private readonly ILogger<SqliteOfflineSampleStore>? _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _initialized;
    private bool _disposed;

    public SqliteOfflineSampleStore(
        AgentOptions options,
        IDataProtectionService dataProtection,
        ILogger<SqliteOfflineSampleStore>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _dataProtection = dataProtection ?? throw new ArgumentNullException(nameof(dataProtection));
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_initialized)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_options.DatabasePath))!);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS agent_state (
                    id INTEGER PRIMARY KEY CHECK (id = 1),
                    next_sequence_number INTEGER NOT NULL DEFAULT 1,
                    pending_dropped_count INTEGER NOT NULL DEFAULT 0,
                    restart_count INTEGER NOT NULL DEFAULT 0,
                    device_id TEXT NULL,
                    encrypted_api_token BLOB NULL
                );
                INSERT OR IGNORE INTO agent_state (id) VALUES (1);
                CREATE TABLE IF NOT EXISTS queued_samples (
                    sequence_number INTEGER PRIMARY KEY,
                    sample_id TEXT NOT NULL UNIQUE,
                    captured_at_utc TEXT NOT NULL,
                    payload_cipher BLOB NOT NULL,
                    stored_bytes INTEGER NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<int> RecordAgentStartupAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE agent_state
                SET restart_count = restart_count + 1
                WHERE id = 1;
                SELECT restart_count FROM agent_state WHERE id = 1;
                """;
            return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<long> AllocateNextSequenceNumberAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction();
            long sequenceNumber;

            await using (var readCommand = connection.CreateCommand())
            {
                readCommand.Transaction = transaction;
                readCommand.CommandText = "SELECT next_sequence_number FROM agent_state WHERE id = 1;";
                sequenceNumber = Convert.ToInt64(await readCommand.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
            }

            await using (var updateCommand = connection.CreateCommand())
            {
                updateCommand.Transaction = transaction;
                updateCommand.CommandText = "UPDATE agent_state SET next_sequence_number = $next WHERE id = 1;";
                updateCommand.Parameters.AddWithValue("$next", checked(sequenceNumber + 1));
                await updateCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return sequenceNumber;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<int> GetQueueDepthAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM queued_samples;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    public async Task<int> GetPendingDroppedCountAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pending_dropped_count FROM agent_state WHERE id = 1;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    public async Task<(int QueueDepth, int DroppedCountThisWrite)> EnqueueSampleAsync(
        TelemetrySample sample,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sample);
        await InitializeAsync(ct).ConfigureAwait(false);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(sample, TelemetryJsonOptions.Default);
            var encryptedPayload = _dataProtection.Protect(payload, sample.SampleId.ToByteArray());
            var storedBytes = checked(encryptedPayload.Length + 128);

            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction();
            var currentBytes = await GetStoredBytesAsync(connection, transaction, ct).ConfigureAwait(false);
            var droppedCount = 0;

            while (currentBytes + storedBytes > _options.MaxOfflineStorageBytes)
            {
                var oldest = await DeleteOldestSampleAsync(connection, transaction, ct).ConfigureAwait(false);
                if (oldest is null)
                {
                    break;
                }

                currentBytes -= oldest.Value;
                droppedCount++;
            }

            if (droppedCount > 0)
            {
                await using var droppedCommand = connection.CreateCommand();
                droppedCommand.Transaction = transaction;
                droppedCommand.CommandText = "UPDATE agent_state SET pending_dropped_count = pending_dropped_count + $count WHERE id = 1;";
                droppedCommand.Parameters.AddWithValue("$count", droppedCount);
                await droppedCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await using (var insertCommand = connection.CreateCommand())
            {
                insertCommand.Transaction = transaction;
                insertCommand.CommandText = """
                    INSERT INTO queued_samples (sequence_number, sample_id, captured_at_utc, payload_cipher, stored_bytes)
                    VALUES ($sequence, $sampleId, $capturedAt, $payload, $storedBytes);
                    """;
                insertCommand.Parameters.AddWithValue("$sequence", sample.SequenceNumber);
                insertCommand.Parameters.AddWithValue("$sampleId", sample.SampleId.ToString("D"));
                insertCommand.Parameters.AddWithValue("$capturedAt", sample.CapturedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                insertCommand.Parameters.Add("$payload", SqliteType.Blob).Value = encryptedPayload;
                insertCommand.Parameters.AddWithValue("$storedBytes", storedBytes);
                await insertCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            int queueDepth;
            await using (var depthCommand = connection.CreateCommand())
            {
                depthCommand.Transaction = transaction;
                depthCommand.CommandText = "SELECT COUNT(*) FROM queued_samples;";
                queueDepth = Convert.ToInt32(await depthCommand.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
            if (droppedCount > 0)
            {
                _logger?.LogWarning(
                    "Offline storage cap removed {DroppedCount} oldest telemetry samples; pending dropped count is updated.",
                    droppedCount);
            }

            return (queueDepth, droppedCount);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<OfflineBatchPeekResult> PeekBatchAsync(int maxBatchSize, CancellationToken ct = default)
    {
        if (maxBatchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBatchSize), "Batch size must be greater than zero.");
        }

        await InitializeAsync(ct).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        var encryptedRows = new List<(Guid SampleId, byte[] Payload)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT sample_id, payload_cipher
                FROM queued_samples
                ORDER BY sequence_number ASC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", maxBatchSize);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                encryptedRows.Add((Guid.Parse(reader.GetString(0)), (byte[])reader[1]));
            }
        }

        var samples = new List<TelemetrySample>(encryptedRows.Count);
        foreach (var row in encryptedRows)
        {
            var plaintext = _dataProtection.Unprotect(row.Payload, row.SampleId.ToByteArray());
            var sample = JsonSerializer.Deserialize<TelemetrySample>(plaintext, TelemetryJsonOptions.Default);
            if (sample is null || sample.SampleId != row.SampleId)
            {
                throw new InvalidDataException("A queued telemetry sample could not be verified.");
            }

            samples.Add(sample);
        }

        var pendingDroppedCount = await GetPendingDroppedCountAsync(ct).ConfigureAwait(false);
        return new OfflineBatchPeekResult(samples, pendingDroppedCount);
    }

    public async Task ConfirmBatchAsync(
        IReadOnlyCollection<Guid> confirmedSampleIds,
        int reportedDroppedCount,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(confirmedSampleIds);
        if (reportedDroppedCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(reportedDroppedCount));
        }

        await InitializeAsync(ct).ConfigureAwait(false);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction();
            foreach (var sampleId in confirmedSampleIds)
            {
                await using var deleteCommand = connection.CreateCommand();
                deleteCommand.Transaction = transaction;
                deleteCommand.CommandText = "DELETE FROM queued_samples WHERE sample_id = $sampleId;";
                deleteCommand.Parameters.AddWithValue("$sampleId", sampleId.ToString("D"));
                await deleteCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await using (var updateCommand = connection.CreateCommand())
            {
                updateCommand.Transaction = transaction;
                updateCommand.CommandText = """
                    UPDATE agent_state
                    SET pending_dropped_count = MAX(0, pending_dropped_count - MIN(pending_dropped_count, $reported))
                    WHERE id = 1;
                    """;
                updateCommand.Parameters.AddWithValue("$reported", reportedDroppedCount);
                await updateCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task SaveCredentialsAsync(string deviceId, string apiToken, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiToken);
        await InitializeAsync(ct).ConfigureAwait(false);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var encryptedToken = _dataProtection.Protect(Encoding.UTF8.GetBytes(apiToken));
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE agent_state SET device_id = $deviceId, encrypted_api_token = $token WHERE id = 1;";
            command.Parameters.AddWithValue("$deviceId", deviceId);
            command.Parameters.Add("$token", SqliteType.Blob).Value = encryptedToken;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<(string DeviceId, string ApiToken)?> GetCredentialsAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT device_id, encrypted_api_token FROM agent_state WHERE id = 1;";
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.IsDBNull(0) || reader.IsDBNull(1))
        {
            return null;
        }

        var deviceId = reader.GetString(0);
        var encryptedToken = (byte[])reader[1];
        var token = Encoding.UTF8.GetString(_dataProtection.Unprotect(encryptedToken));
        return (deviceId, token);
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _writeLock.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken ct)
    {
        ThrowIfDisposed();
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString());

        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var pragma = connection.CreateCommand();
            pragma.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=FULL;
                PRAGMA busy_timeout=5000;
                """;
            await pragma.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<long> GetStoredBytesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(SUM(stored_bytes), 0) FROM queued_samples;";
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static async Task<long?> DeleteOldestSampleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM queued_samples
            WHERE sequence_number = (
                SELECT sequence_number FROM queued_samples ORDER BY sequence_number ASC LIMIT 1
            )
            RETURNING stored_bytes;
            """;
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is null or DBNull
            ? null
            : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
