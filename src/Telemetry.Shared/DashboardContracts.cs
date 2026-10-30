using System;
using System.Collections.Generic;

namespace Telemetry.Shared;

/// <summary>
/// Device health states shown in the operational dashboard.
/// </summary>
public enum DeviceStatus
{
    Online,
    Stale,
    Offline
}

/// <summary>Summarizes an enrolled device and its most recent observed state.</summary>
public record DeviceSummaryDto(
    string DeviceId,
    string DeviceName,
    DateTimeOffset EnrolledAtUtc,
    DateTimeOffset? LastSeenAtUtc,
    string Status,
    string AgentVersion,
    SessionState CurrentSessionState);

/// <summary>Represents one telemetry sample stored by the server.</summary>
public record StoredSampleDto(
    string DeviceId,
    DateTimeOffset ReceivedAtUtc,
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

/// <summary>Represents one CPU and memory measurement in a dashboard history.</summary>
public record MetricPointDto(DateTimeOffset CapturedAtUtc, decimal CpuPercent, int MemoryUsedMb);

/// <summary>Represents a contiguous period in a device session-state timeline.</summary>
public record SessionBandDto(DateTimeOffset StartUtc, DateTimeOffset EndUtc, SessionState State, int SampleCount);

/// <summary>Summarizes foreground application activity for a process.</summary>
public record AppUsageItemDto(string ProcessName, string DisplayName, int ActiveSeconds, int SampleCount);

/// <summary>Summarizes active, idle, and locked time for a selected dashboard range.</summary>
public record AppUsageSummaryDto(
    int TotalActiveSeconds,
    int TotalIdleSeconds,
    int TotalLockedSeconds,
    int IdleThresholdSeconds,
    IReadOnlyList<AppUsageItemDto> TopApps);

/// <summary>Describes a missing range of device sequence numbers.</summary>
public record GapRecordDto(string DeviceId, long FromSequence, long ToSequence, long MissingCount, DateTimeOffset DetectedAtUtc);

/// <summary>Summarizes received batches, stored samples, duplicates, drops, and sequence gaps.</summary>
public record DeliveryStatsDto(
    long SamplesStored,
    long DuplicatesIgnored,
    long SamplesDroppedByCap,
    long BatchesReceived,
    IReadOnlyList<GapRecordDto> MissingSampleGaps);

/// <summary>Describes a rejected API request without including secret credentials.</summary>
public record RejectedRequestDto(long Id, DateTimeOffset RejectedAtUtc, string Endpoint, int HttpStatus, string Reason, string? DeviceId);

/// <summary>Summarizes the agent process and most recent successful synchronization.</summary>
public record AgentHealthDto(
    string DeviceId,
    DateTimeOffset? AgentStartedAtUtc,
    int RestartCount,
    int CurrentQueueDepth,
    DateTimeOffset? LastSuccessfulSyncUtc);

/// <summary>Contains the aggregated read-only dashboard view for the selected devices and range.</summary>
public record DashboardOverviewDto(
    IReadOnlyList<DeviceSummaryDto> DeviceStatus,
    IReadOnlyList<StoredSampleDto> RecentSamples,
    IReadOnlyList<MetricPointDto> CpuHistory,
    IReadOnlyList<SessionBandDto> SessionBands,
    AppUsageSummaryDto AppUsageSummary,
    IReadOnlyList<GapRecordDto> MissingSampleGaps,
    DeliveryStatsDto DeliveryStats,
    IReadOnlyList<RejectedRequestDto> RejectedRequests,
    IReadOnlyList<AgentHealthDto> AgentHealth);
