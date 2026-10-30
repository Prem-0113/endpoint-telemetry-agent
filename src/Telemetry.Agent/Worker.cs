using Microsoft.Extensions.Logging.Abstractions;
using Telemetry.Agent.Collectors;
using Telemetry.Agent.Configuration;
using Telemetry.Agent.Logging;
using Telemetry.Agent.Storage;
using Telemetry.Agent.Sync;
using Telemetry.Shared;

namespace Telemetry.Agent;

public sealed class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly AgentOptions _options;
    private readonly IOfflineSampleStore _store;
    private readonly IMachineMetricsProvider _machineMetrics;
    private readonly IUserSessionProvider _userSession;
    private readonly TelemetrySyncOrchestrator _syncOrchestrator;

    public Worker(
        ILogger<Worker> logger,
        AgentOptions options,
        IOfflineSampleStore store,
        IMachineMetricsProvider machineMetrics,
        IUserSessionProvider userSession,
        TelemetrySyncOrchestrator syncOrchestrator)
    {
        _logger = logger;
        _options = options;
        _store = store;
        _machineMetrics = machineMetrics;
        _userSession = userSession;
        _syncOrchestrator = syncOrchestrator;
        AgentStartedAtUtc = DateTimeOffset.UtcNow;
    }

    public DateTimeOffset AgentStartedAtUtc { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _store.InitializeAsync(stoppingToken).ConfigureAwait(false);
        var restartCount = await _store.RecordAgentStartupAsync(stoppingToken).ConfigureAwait(false);
        AgentStartedAtUtc = DateTimeOffset.UtcNow;
        AgentLogEvents.LogServiceStarted(_logger, restartCount);

        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, _options.SampleIntervalSeconds)));
            do
            {
                await CaptureAndStoreSampleAsync(stoppingToken).ConfigureAwait(false);
                await _syncOrchestrator.SyncPendingBatchAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Agent shutdown requested; current cycle has stopped cleanly.");
        }
        finally
        {
            AgentLogEvents.LogServiceStopped(_logger);
        }
    }

    public async Task<TelemetrySample> CaptureAndStoreSampleAsync(CancellationToken ct = default)
    {
        var sequenceNumber = await _store.AllocateNextSequenceNumberAsync(ct).ConfigureAwait(false);
        var (cpuPercent, memoryUsedMb) = _machineMetrics.GetMetrics();
        var capturedAtUtc = DateTimeOffset.UtcNow;
        var session = await _userSession.GetSnapshotAsync(capturedAtUtc, ct).ConfigureAwait(false);
        var queueDepth = await _store.GetQueueDepthAsync(ct).ConfigureAwait(false) + 1;
        var sample = new TelemetrySample(
            Guid.NewGuid(),
            capturedAtUtc,
            Math.Clamp(cpuPercent, 0m, 100m),
            Math.Max(1, memoryUsedMb),
            session.SessionState,
            session.IdleSeconds,
            session.ForegroundProcess,
            session.ForegroundAppName,
            TelemetryConstants.DefaultAgentVersion,
            sequenceNumber,
            AgentStartedAtUtc,
            queueDepth);

        var enqueueResult = await _store.EnqueueSampleAsync(sample, ct).ConfigureAwait(false);
        if (enqueueResult.DroppedCountThisWrite > 0)
        {
            AgentLogEvents.LogSamplesDropped(_logger, enqueueResult.DroppedCountThisWrite, _options.MaxOfflineStorageBytes);
        }

        AgentLogEvents.LogSampleCaptured(
            _logger,
            sample.SampleId,
            sample.SequenceNumber,
            sample.SessionState,
            sample.CpuPercent,
            sample.MemoryUsedMb,
            enqueueResult.QueueDepth);
        return sample;
    }
}
