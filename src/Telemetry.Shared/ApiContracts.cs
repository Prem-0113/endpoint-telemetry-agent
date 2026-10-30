using System;
using System.Collections.Generic;

namespace Telemetry.Shared;

/// <summary>
/// Device enrollment request used to register a new endpoint with the server.
/// </summary>
public record EnrollRequest(string DeviceName, string MachineGuid, string OsVersion, string AgentVersion);

/// <summary>
/// Device enrollment response returned after a successful registration.
/// </summary>
public record EnrollResponse(string DeviceId, string ApiToken, DateTimeOffset EnrolledAtUtc);

/// <summary>
/// Batch upload contract used by the agent to send telemetry samples to the server.
/// </summary>
public record BatchUploadRequest(string DeviceId, int DroppedSamplesCount, IReadOnlyList<TelemetrySample> Samples);

/// <summary>
/// Result for accepted telemetry batches.
/// </summary>
public record BatchUploadResponse(int StoredCount, int DuplicateCount, IReadOnlyList<Guid> ConfirmedSampleIds, DateTimeOffset ServerReceivedAtUtc);

/// <summary>
/// Standard error payload returned by the API for rejected requests.
/// </summary>
public record ApiErrorResponse(string Error, string Reason, int StatusCode, DateTimeOffset TimestampUtc);

/// <summary>
/// Snapshot of the interactive user session shared between the Session 0 service and the Session 1 probe.
/// </summary>
public record UserSessionSnapshot(
    SessionState SessionState,
    int? IdleSeconds,
    string? ForegroundProcess,
    string? ForegroundAppName,
    DateTimeOffset CapturedAtUtc);
