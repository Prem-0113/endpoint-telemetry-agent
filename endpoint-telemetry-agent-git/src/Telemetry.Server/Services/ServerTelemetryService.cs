using Telemetry.Server.Storage;
using Telemetry.Shared;

namespace Telemetry.Server.Services;

public sealed class ServerTelemetryService : IServerTelemetryService
{
    private readonly IServerTelemetryStore _store;

    public ServerTelemetryService(IServerTelemetryStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public Task<EnrollResponse> EnrollAsync(
        EnrollRequest request,
        DateTimeOffset nowUtc,
        CancellationToken ct = default) =>
        _store.EnrollDeviceAsync(request, nowUtc, ct);

    public Task<bool> ValidateCredentialsAsync(string deviceId, string apiToken, CancellationToken ct = default) =>
        _store.ValidateCredentialsAsync(deviceId, apiToken, ct);

    public Task<BatchUploadResponse> IngestBatchAsync(
        string deviceId,
        BatchUploadRequest request,
        DateTimeOffset nowUtc,
        CancellationToken ct = default) =>
        _store.IngestBatchAsync(deviceId, request, nowUtc, ct);

    public Task RejectAsync(
        string endpoint,
        int statusCode,
        string reason,
        string? deviceId,
        DateTimeOffset nowUtc,
        CancellationToken ct = default) =>
        _store.RecordRejectedRequestAsync(endpoint, statusCode, reason, deviceId, nowUtc, ct);
}
