using Telemetry.Shared;

namespace Telemetry.Server.Storage;

public sealed record ServerDeviceRecord(
    string DeviceId,
    string DeviceName,
    DateTimeOffset EnrolledAtUtc,
    DateTimeOffset? LastSeenAtUtc,
    DateTimeOffset? LastSyncAtUtc,
    DateTimeOffset? AgentStartedAtUtc,
    int RestartCount,
    int CurrentQueueDepth,
    SessionState CurrentSessionState,
    long SamplesStored,
    long DuplicatesIgnored,
    long SamplesDroppedByCap,
    long BatchesReceived,
    string AgentVersion);

public sealed record ServerRejectedRequestRecord(
    long Id,
    DateTimeOffset RejectedAtUtc,
    string Endpoint,
    int HttpStatus,
    string Reason,
    string? DeviceId);
