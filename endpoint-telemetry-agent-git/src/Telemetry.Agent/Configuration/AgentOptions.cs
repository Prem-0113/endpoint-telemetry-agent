using Telemetry.Shared;

namespace Telemetry.Agent.Configuration;

public sealed class AgentOptions
{
    public string DataDirectory { get; set; } = "data";
    public string DatabaseFileName { get; set; } = "agent-telemetry.db";
    public string MasterKeyFileName { get; set; } = "agent-master.key";
    public long MaxOfflineStorageBytes { get; set; } = TelemetryConstants.DefaultMaxOfflineStorageBytes;
    public int SampleIntervalSeconds { get; set; } = TelemetryConstants.DefaultSampleIntervalSeconds;
    public int SyncBatchSize { get; set; } = TelemetryConstants.DefaultSyncBatchSize;
    public int IdleThresholdSeconds { get; set; } = TelemetryConstants.DefaultIdleThresholdSeconds;
    public string ServerBaseUrl { get; set; } = "http://localhost:5080";
    public int RetryBaseDelayMs { get; set; } = 2000;
    public int RetryMaxDelayMs { get; set; } = 30000;
    public int MaxRetriesPerCycle { get; set; } = 3;
    public int HttpTimeoutSeconds { get; set; } = 10;
    public string? DeviceNameOverride { get; set; }

    public string DatabasePath => Path.Combine(DataDirectory, DatabaseFileName);
    public string MasterKeyPath => Path.Combine(DataDirectory, MasterKeyFileName);
}
