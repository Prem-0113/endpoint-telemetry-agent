using System;

namespace Telemetry.Shared;

/// <summary>
/// A single machine telemetry observation captured by the agent. The sequence number is monotonic per device and is used
/// as a durable ordering guarantee across restarts and retry cycles.
/// </summary>
public record TelemetrySample(
    Guid SampleId,
    DateTimeOffset CapturedAtUtc,
    decimal CpuPercent,
    int MemoryUsedMb,
    SessionState SessionState,
    int? IdleSeconds,
    string? ForegroundProcess,
    string? ForegroundAppName,
    string AgentVersion,
    long SequenceNumber,
    DateTimeOffset AgentStartedAtUtc,
    int QueueDepth);
