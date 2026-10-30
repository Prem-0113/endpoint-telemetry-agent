using Telemetry.Shared;

namespace Telemetry.Server.Services;

public interface IServerTelemetryService
{
    Task<EnrollResponse> EnrollAsync(EnrollRequest request, DateTimeOffset nowUtc, CancellationToken ct = default);
    Task<bool> ValidateCredentialsAsync(string deviceId, string apiToken, CancellationToken ct = default);
    Task<BatchUploadResponse> IngestBatchAsync(
        string deviceId,
        BatchUploadRequest request,
        DateTimeOffset nowUtc,
        CancellationToken ct = default);
    Task RejectAsync(
        string endpoint,
        int statusCode,
        string reason,
        string? deviceId,
        DateTimeOffset nowUtc,
        CancellationToken ct = default);
}
