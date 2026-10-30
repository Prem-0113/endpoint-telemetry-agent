using Telemetry.Shared;

namespace Telemetry.Server.Storage;

public interface IServerTelemetryStore
{
    Task InitializeAsync(CancellationToken ct = default);
    Task<EnrollResponse> EnrollDeviceAsync(EnrollRequest request, DateTimeOffset nowUtc, CancellationToken ct = default);
    Task<bool> ValidateCredentialsAsync(string deviceId, string apiToken, CancellationToken ct = default);
    Task<BatchUploadResponse> IngestBatchAsync(
        string deviceId,
        BatchUploadRequest request,
        DateTimeOffset nowUtc,
        CancellationToken ct = default);
    Task RecordRejectedRequestAsync(
        string endpoint,
        int httpStatus,
        string reason,
        string? deviceId,
        DateTimeOffset nowUtc,
        CancellationToken ct = default);
    Task<IReadOnlyList<ServerDeviceRecord>> GetDevicesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<StoredSampleDto>> GetSamplesAsync(string? deviceId = null, CancellationToken ct = default);
    Task<IReadOnlyList<ServerRejectedRequestRecord>> GetRejectedRequestsAsync(CancellationToken ct = default);
}
