using Telemetry.Shared;

namespace Telemetry.Agent.Storage;

public interface IOfflineSampleStore
{
    Task InitializeAsync(CancellationToken ct = default);

    Task<int> RecordAgentStartupAsync(CancellationToken ct = default);

    Task<long> AllocateNextSequenceNumberAsync(CancellationToken ct = default);

    Task<int> GetQueueDepthAsync(CancellationToken ct = default);

    Task<int> GetPendingDroppedCountAsync(CancellationToken ct = default);

    Task<(int QueueDepth, int DroppedCountThisWrite)> EnqueueSampleAsync(
        TelemetrySample sample,
        CancellationToken ct = default);

    Task<OfflineBatchPeekResult> PeekBatchAsync(int maxBatchSize, CancellationToken ct = default);

    Task ConfirmBatchAsync(IReadOnlyCollection<Guid> confirmedSampleIds, int reportedDroppedCount, CancellationToken ct = default);

    Task SaveCredentialsAsync(string deviceId, string apiToken, CancellationToken ct = default);

    Task<(string DeviceId, string ApiToken)?> GetCredentialsAsync(CancellationToken ct = default);
}
