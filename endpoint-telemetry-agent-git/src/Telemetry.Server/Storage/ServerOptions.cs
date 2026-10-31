using Telemetry.Shared;

namespace Telemetry.Server.Storage;

public sealed class ServerOptions
{
    public string DataDirectory { get; set; } = "data";
    public string DatabaseFileName { get; set; } = "server-telemetry.db";
    public int IdleThresholdSeconds { get; set; } = TelemetryConstants.DefaultIdleThresholdSeconds;
    public int MaxBatchRequestBytes { get; set; } = TelemetryConstants.MaxBatchRequestBytes;

    public string DatabasePath => Path.Combine(DataDirectory, DatabaseFileName);
}
