using System;

namespace Telemetry.Shared;

/// <summary>
/// Centralized defaults for the telemetry pipeline so tuning can be done in one place during design reviews.
/// </summary>
public static class TelemetryConstants
{
    public const string DefaultAgentVersion = "1.0.0";
    public const int DefaultSampleIntervalSeconds = 60;
    public const int DefaultIdleThresholdSeconds = 300;
    public const long DefaultMaxOfflineStorageBytes = 50L * 1024 * 1024;
    public const int DefaultSyncBatchSize = 50;
    public const int MaxBatchRequestBytes = 1024 * 1024;
    public const int MaxBatchSampleCount = 500;
    public const int MaxIpcMessageBytes = 4096;
    public static readonly TimeSpan MaxFutureClockSkew = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MaxPastSampleAge = TimeSpan.FromDays(30);
    public static readonly TimeSpan OnlineThreshold = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan StaleThreshold = TimeSpan.FromMinutes(5);
}
