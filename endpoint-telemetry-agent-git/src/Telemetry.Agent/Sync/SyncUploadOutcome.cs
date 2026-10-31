using Telemetry.Shared;

namespace Telemetry.Agent.Sync;

public enum SyncUploadStatus
{
    Success,
    TransientFailure,
    PermanentFailure
}

public sealed record SyncUploadOutcome(
    SyncUploadStatus Status,
    BatchUploadResponse? Response = null,
    int? HttpStatusCode = null,
    string? Reason = null)
{
    public static SyncUploadOutcome Succeeded(BatchUploadResponse response) =>
        new(SyncUploadStatus.Success, response);

    public static SyncUploadOutcome Transient(string reason, int? statusCode = null) =>
        new(SyncUploadStatus.TransientFailure, HttpStatusCode: statusCode, Reason: reason);

    public static SyncUploadOutcome Rejected(string reason, int? statusCode = null) =>
        new(SyncUploadStatus.PermanentFailure, HttpStatusCode: statusCode, Reason: reason);
}
