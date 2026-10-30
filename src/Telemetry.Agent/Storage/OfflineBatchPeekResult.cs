using Telemetry.Shared;

namespace Telemetry.Agent.Storage;

public sealed record OfflineBatchPeekResult(IReadOnlyList<TelemetrySample> Samples, int PendingDroppedCount);
