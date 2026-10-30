using Telemetry.Shared;

namespace Telemetry.Agent.Sync;

public interface ITelemetrySyncClient
{
    Task<EnrollResponse> EnrollAsync(EnrollRequest request, CancellationToken ct);

    Task<SyncUploadOutcome> UploadBatchAsync(BatchUploadRequest request, string apiToken, CancellationToken ct);
}
