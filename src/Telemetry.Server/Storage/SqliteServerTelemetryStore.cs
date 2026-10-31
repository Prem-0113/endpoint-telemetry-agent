using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Telemetry.Shared;

namespace Telemetry.Server.Storage;

public sealed class SqliteServerTelemetryStore : IServerTelemetryStore, IAsyncDisposable
{
    private const string BatchEndpoint = "/api/v1/telemetry/batch";
    private readonly ServerOptions _options;
    private readonly ILogger<SqliteServerTelemetryStore>? _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _initialized;
    private bool _disposed;

    public SqliteServerTelemetryStore(ServerOptions options, ILogger<SqliteServerTelemetryStore>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
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
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=FULL;
                PRAGMA busy_timeout=5000;
                CREATE TABLE IF NOT EXISTS devices (
                    device_id TEXT PRIMARY KEY,
                    device_name TEXT NOT NULL,
                    machine_guid TEXT NOT NULL,
                    os_version TEXT NOT NULL,
                    agent_version TEXT NOT NULL,
                    api_token_hash TEXT NOT NULL,
                    enrolled_at_utc TEXT NOT NULL,
                    last_seen_at_utc TEXT NULL,
                    last_sync_at_utc TEXT NULL,
                    agent_started_at_utc TEXT NULL,
                    restart_count INTEGER NOT NULL DEFAULT 0,
                    current_queue_depth INTEGER NOT NULL DEFAULT 0,
                    current_session_state TEXT NOT NULL DEFAULT 'NoUser',
                    samples_stored INTEGER NOT NULL DEFAULT 0,
                    duplicates_ignored INTEGER NOT NULL DEFAULT 0,
                    samples_dropped_by_cap INTEGER NOT NULL DEFAULT 0,
                    batches_received INTEGER NOT NULL DEFAULT 0
                );
                CREATE TABLE IF NOT EXISTS samples (
                    sample_id TEXT PRIMARY KEY,
                    device_id TEXT NOT NULL,
                    sequence_number INTEGER NOT NULL,
                    captured_at_utc TEXT NOT NULL,
                    received_at_utc TEXT NOT NULL,
                    cpu_percent REAL NOT NULL,
                    memory_used_mb INTEGER NOT NULL,
                    session_state TEXT NOT NULL,
                    idle_seconds INTEGER NULL,
                    foreground_process TEXT NULL,
                    foreground_app_name TEXT NULL,
                    agent_version TEXT NOT NULL,
                    agent_started_at_utc TEXT NOT NULL,
                    queue_depth INTEGER NOT NULL,
                    UNIQUE(device_id, sequence_number),
                    FOREIGN KEY(device_id) REFERENCES devices(device_id)
                );
                CREATE INDEX IF NOT EXISTS ix_samples_device_captured
                    ON samples(device_id, captured_at_utc);
                CREATE TABLE IF NOT EXISTS rejected_requests (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    rejected_at_utc TEXT NOT NULL,
                    endpoint TEXT NOT NULL,
                    http_status INTEGER NOT NULL,
                    reason TEXT NOT NULL,
                    device_id TEXT NULL
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

    public async Task<EnrollResponse> EnrollDeviceAsync(
        EnrollRequest request,
        DateTimeOffset nowUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.DeviceName) ||
            string.IsNullOrWhiteSpace(request.MachineGuid) ||
            string.IsNullOrWhiteSpace(request.AgentVersion))
        {
            throw new ArgumentException("DeviceName, MachineGuid, and AgentVersion are required.", nameof(request));
        }

        var deviceId = "dev-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.MachineGuid)))[..16].ToLowerInvariant();
        var apiToken = EncodeBase64Url(RandomNumberGenerator.GetBytes(32));
        var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiToken)));
        var enrolledAtUtc = nowUtc.ToUniversalTime();

        await InitializeAsync(ct).ConfigureAwait(false);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO devices (
                    device_id, device_name, machine_guid, os_version, agent_version,
                    api_token_hash, enrolled_at_utc
                ) VALUES (
                    $deviceId, $deviceName, $machineGuid, $osVersion, $agentVersion,
                    $tokenHash, $enrolledAt
                )
                ON CONFLICT(device_id) DO UPDATE SET
                    device_name = excluded.device_name,
                    machine_guid = excluded.machine_guid,
                    os_version = excluded.os_version,
                    agent_version = excluded.agent_version,
                    api_token_hash = excluded.api_token_hash;
                """;
            command.Parameters.AddWithValue("$deviceId", deviceId);
            command.Parameters.AddWithValue("$deviceName", request.DeviceName.Trim());
            command.Parameters.AddWithValue("$machineGuid", request.MachineGuid.Trim());
            command.Parameters.AddWithValue("$osVersion", request.OsVersion ?? string.Empty);
            command.Parameters.AddWithValue("$agentVersion", request.AgentVersion.Trim());
            command.Parameters.AddWithValue("$tokenHash", tokenHash);
            command.Parameters.AddWithValue("$enrolledAt", FormatUtc(enrolledAtUtc));
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }

        _logger?.LogInformation("Device enrolled. DeviceId: {DeviceId}", deviceId);
        return new EnrollResponse(deviceId, apiToken, enrolledAtUtc);
    }

    public async Task<bool> ValidateCredentialsAsync(string deviceId, string apiToken, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiToken);
        await InitializeAsync(ct).ConfigureAwait(false);

        string? storedHash;
        await using (var connection = await OpenConnectionAsync(ct).ConfigureAwait(false))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT api_token_hash FROM devices WHERE device_id = $deviceId;";
            command.Parameters.AddWithValue("$deviceId", deviceId);
            storedHash = await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        }

        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(apiToken));
        var expectedHash = storedHash is null
            ? new byte[32]
            : Convert.FromHexString(storedHash);
        return CryptographicOperations.FixedTimeEquals(suppliedHash, expectedHash) && storedHash is not null;
    }

    public async Task<BatchUploadResponse> IngestBatchAsync(
        string deviceId,
        BatchUploadRequest request,
        DateTimeOffset nowUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Samples is null || request.Samples.Count == 0)
        {
            throw new ArgumentException("Batch must contain at least one sample.", nameof(request));
        }

        if (request.Samples.Count > TelemetryConstants.MaxBatchSampleCount)
        {
            throw new ArgumentException($"Batch cannot contain more than {TelemetryConstants.MaxBatchSampleCount} samples.", nameof(request));
        }

        var receivedAt = nowUtc.ToUniversalTime();
        foreach (var sample in request.Samples)
        {
            if (!TelemetryValidator.TryValidateSample(sample, receivedAt, out var errorReason))
            {
                var reason = errorReason ?? "Sample is invalid.";
                await RecordRejectedRequestAsync(BatchEndpoint, 400, reason, deviceId, receivedAt, ct).ConfigureAwait(false);
                throw new BatchValidationException(reason);
            }
        }

        await InitializeAsync(ct).ConfigureAwait(false);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction();
            if (!await DeviceExistsAsync(connection, transaction, deviceId, ct).ConfigureAwait(false))
            {
                throw new KeyNotFoundException("Device is not enrolled.");
            }

            var storedCount = 0;
            var duplicateCount = 0;
            var confirmedIds = new List<Guid>(request.Samples.Count);
            DateTimeOffset? latestCapturedAt = null;
            TelemetrySample? latestSample = null;

            await using (var batchCommand = connection.CreateCommand())
            {
                batchCommand.Transaction = (SqliteTransaction)transaction;
                batchCommand.CommandText = """
                    UPDATE devices
                    SET batches_received = batches_received + 1,
                        samples_dropped_by_cap = samples_dropped_by_cap + $dropped
                    WHERE device_id = $deviceId;
                    """;
                batchCommand.Parameters.AddWithValue("$dropped", Math.Max(0, request.DroppedSamplesCount));
                batchCommand.Parameters.AddWithValue("$deviceId", deviceId);
                await batchCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            foreach (var sample in request.Samples)
            {
                var sampleCapturedAt = sample.CapturedAtUtc.ToUniversalTime();
                if (latestCapturedAt is null || sampleCapturedAt > latestCapturedAt)
                {
                    latestCapturedAt = sampleCapturedAt;
                    latestSample = sample;
                }

                if (await SampleExistsAsync(connection, (SqliteTransaction)transaction, deviceId, sample, ct).ConfigureAwait(false))
                {
                    duplicateCount++;
                    confirmedIds.Add(sample.SampleId);
                    continue;
                }

                await InsertSampleAsync(connection, (SqliteTransaction)transaction, deviceId, sample, receivedAt, ct).ConfigureAwait(false);
                storedCount++;
                confirmedIds.Add(sample.SampleId);

                await UpdateRestartStateAsync(connection, (SqliteTransaction)transaction, deviceId, sample.AgentStartedAtUtc, ct)
                    .ConfigureAwait(false);
            }

            if (latestSample is not null && latestCapturedAt is not null)
            {
                await using var update = connection.CreateCommand();
                update.Transaction = (SqliteTransaction)transaction;
                update.CommandText = """
                    UPDATE devices
                    SET last_seen_at_utc = CASE
                            WHEN last_seen_at_utc IS NULL OR last_seen_at_utc < $lastSeen THEN $lastSeen
                            ELSE last_seen_at_utc
                        END,
                        last_sync_at_utc = $lastSync,
                        current_queue_depth = $queueDepth,
                        current_session_state = $sessionState,
                        samples_stored = samples_stored + $stored,
                        duplicates_ignored = duplicates_ignored + $duplicates
                    WHERE device_id = $deviceId;
                    """;
                update.Parameters.AddWithValue("$lastSeen", FormatUtc(latestCapturedAt.Value));
                update.Parameters.AddWithValue("$lastSync", FormatUtc(receivedAt));
                update.Parameters.AddWithValue("$queueDepth", latestSample.QueueDepth);
                update.Parameters.AddWithValue("$sessionState", latestSample.SessionState.ToString());
                update.Parameters.AddWithValue("$stored", storedCount);
                update.Parameters.AddWithValue("$duplicates", duplicateCount);
                update.Parameters.AddWithValue("$deviceId", deviceId);
                await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return new BatchUploadResponse(storedCount, duplicateCount, confirmedIds, receivedAt);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task RecordRejectedRequestAsync(
        string endpoint,
        int httpStatus,
        string reason,
        string? deviceId,
        DateTimeOffset nowUtc,
        CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO rejected_requests (rejected_at_utc, endpoint, http_status, reason, device_id)
                VALUES ($rejectedAt, $endpoint, $status, $reason, $deviceId);
                """;
            command.Parameters.AddWithValue("$rejectedAt", FormatUtc(nowUtc));
            command.Parameters.AddWithValue("$endpoint", endpoint);
            command.Parameters.AddWithValue("$status", httpStatus);
            command.Parameters.AddWithValue("$reason", reason);
            command.Parameters.AddWithValue("$deviceId", (object?)deviceId ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }

        _logger?.LogWarning(
            "Request rejected. Endpoint: {Endpoint}, StatusCode: {StatusCode}, Reason: {Reason}, DeviceId: {DeviceId}",
            endpoint,
            httpStatus,
            reason,
            deviceId);
    }

    public async Task<IReadOnlyList<ServerDeviceRecord>> GetDevicesAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);
        var devices = new List<ServerDeviceRecord>();
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT device_id, device_name, enrolled_at_utc, last_seen_at_utc, last_sync_at_utc,
                   agent_started_at_utc, restart_count, current_queue_depth, current_session_state,
                   samples_stored, duplicates_ignored, samples_dropped_by_cap, batches_received, agent_version
            FROM devices ORDER BY device_name COLLATE NOCASE, device_id;
            """;
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            devices.Add(new ServerDeviceRecord(
                reader.GetString(0),
                reader.GetString(1),
                ParseUtc(reader.GetString(2)),
                ReadNullableDate(reader, 3),
                ReadNullableDate(reader, 4),
                ReadNullableDate(reader, 5),
                reader.GetInt32(6),
                reader.GetInt32(7),
                Enum.Parse<SessionState>(reader.GetString(8)),
                reader.GetInt64(9),
                reader.GetInt64(10),
                reader.GetInt64(11),
                reader.GetInt64(12),
                reader.GetString(13)));
        }

        return devices;
    }

    public async Task<IReadOnlyList<StoredSampleDto>> GetSamplesAsync(
        string? deviceId = null,
        CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);
        var samples = new List<StoredSampleDto>();
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT device_id, received_at_utc, sample_id, captured_at_utc, cpu_percent, memory_used_mb,
                   session_state, idle_seconds, foreground_process, foreground_app_name, agent_version,
                   sequence_number, agent_started_at_utc, queue_depth
            FROM samples
            WHERE $deviceId IS NULL OR device_id = $deviceId
            ORDER BY captured_at_utc ASC, sequence_number ASC;
            """;
        command.Parameters.AddWithValue("$deviceId", (object?)deviceId ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            samples.Add(new StoredSampleDto(
                reader.GetString(0),
                ParseUtc(reader.GetString(1)),
                Guid.Parse(reader.GetString(2)),
                ParseUtc(reader.GetString(3)),
                Convert.ToDecimal(reader.GetDouble(4), CultureInfo.InvariantCulture),
                reader.GetInt32(5),
                Enum.Parse<SessionState>(reader.GetString(6)),
                reader.IsDBNull(7) ? null : reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.GetString(10),
                reader.GetInt64(11),
                ParseUtc(reader.GetString(12)),
                reader.GetInt32(13)));
        }

        return samples;
    }

    public async Task<IReadOnlyList<ServerRejectedRequestRecord>> GetRejectedRequestsAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct).ConfigureAwait(false);
        var requests = new List<ServerRejectedRequestRecord>();
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, rejected_at_utc, endpoint, http_status, reason, device_id
            FROM rejected_requests ORDER BY id DESC LIMIT 200;
            """;
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            requests.Add(new ServerRejectedRequestRecord(
                reader.GetInt64(0),
                ParseUtc(reader.GetString(1)),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return requests;
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _writeLock.Dispose();
            _disposed = true;
        }

        return ValueTask.CompletedTask;
    }

    private static async Task<bool> DeviceExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string deviceId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM devices WHERE device_id = $deviceId;";
        command.Parameters.AddWithValue("$deviceId", deviceId);
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
    }

    private static async Task<bool> SampleExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string deviceId,
        TelemetrySample sample,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1 FROM samples
            WHERE sample_id = $sampleId OR (device_id = $deviceId AND sequence_number = $sequence);
            """;
        command.Parameters.AddWithValue("$sampleId", sample.SampleId.ToString("D"));
        command.Parameters.AddWithValue("$deviceId", deviceId);
        command.Parameters.AddWithValue("$sequence", sample.SequenceNumber);
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
    }

    private static async Task InsertSampleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string deviceId,
        TelemetrySample sample,
        DateTimeOffset receivedAt,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO samples (
                sample_id, device_id, sequence_number, captured_at_utc, received_at_utc,
                cpu_percent, memory_used_mb, session_state, idle_seconds, foreground_process,
                foreground_app_name, agent_version, agent_started_at_utc, queue_depth
            ) VALUES (
                $sampleId, $deviceId, $sequence, $capturedAt, $receivedAt,
                $cpu, $memory, $state, $idle, $process, $app,
                $agentVersion, $agentStarted, $queueDepth
            );
            """;
        command.Parameters.AddWithValue("$sampleId", sample.SampleId.ToString("D"));
        command.Parameters.AddWithValue("$deviceId", deviceId);
        command.Parameters.AddWithValue("$sequence", sample.SequenceNumber);
        command.Parameters.AddWithValue("$capturedAt", FormatUtc(sample.CapturedAtUtc));
        command.Parameters.AddWithValue("$receivedAt", FormatUtc(receivedAt));
        command.Parameters.AddWithValue("$cpu", (double)sample.CpuPercent);
        command.Parameters.AddWithValue("$memory", sample.MemoryUsedMb);
        command.Parameters.AddWithValue("$state", sample.SessionState.ToString());
        command.Parameters.AddWithValue("$idle", (object?)sample.IdleSeconds ?? DBNull.Value);
        command.Parameters.AddWithValue("$process", (object?)sample.ForegroundProcess ?? DBNull.Value);
        command.Parameters.AddWithValue("$app", (object?)sample.ForegroundAppName ?? DBNull.Value);
        command.Parameters.AddWithValue("$agentVersion", sample.AgentVersion);
        command.Parameters.AddWithValue("$agentStarted", FormatUtc(sample.AgentStartedAtUtc));
        command.Parameters.AddWithValue("$queueDepth", sample.QueueDepth);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task UpdateRestartStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string deviceId,
        DateTimeOffset agentStartedAtUtc,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE devices
            SET restart_count = CASE
                    WHEN agent_started_at_utc IS NULL THEN MAX(1, restart_count)
                    WHEN julianday($agentStarted) > julianday(agent_started_at_utc) + (1.0 / 86400.0)
                        THEN restart_count + 1
                    ELSE restart_count
                END,
                agent_started_at_utc = CASE
                    WHEN agent_started_at_utc IS NULL OR
                         julianday($agentStarted) > julianday(agent_started_at_utc) + (1.0 / 86400.0)
                        THEN $agentStarted
                    ELSE agent_started_at_utc
                END
            WHERE device_id = $deviceId;
            """;
        command.Parameters.AddWithValue("$agentStarted", FormatUtc(agentStartedAtUtc));
        command.Parameters.AddWithValue("$deviceId", deviceId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=5000; PRAGMA synchronous=FULL;";
        await pragma.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return connection;
    }

    private static string EncodeBase64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string FormatUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

    private static DateTimeOffset? ReadNullableDate(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ParseUtc(reader.GetString(ordinal));

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

public sealed class BatchValidationException(string message) : Exception(message);
