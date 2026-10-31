using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Telemetry.Agent.Configuration;
using Telemetry.Agent.Logging;
using Telemetry.Agent.Storage;
using Telemetry.Shared;

namespace Telemetry.Agent.Sync;

public sealed class TelemetrySyncOrchestrator
{
    private readonly AgentOptions _options;
    private readonly IOfflineSampleStore _store;
    private readonly ITelemetrySyncClient _client;
    private readonly ExponentialBackoffPolicy _backoff;
    private readonly ILogger<TelemetrySyncOrchestrator>? _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

    public TelemetrySyncOrchestrator(
        AgentOptions options,
        IOfflineSampleStore store,
        ITelemetrySyncClient client,
        ExponentialBackoffPolicy backoff,
        ILogger<TelemetrySyncOrchestrator>? logger = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _backoff = backoff ?? throw new ArgumentNullException(nameof(backoff));
        _logger = logger;
        _delayAsync = delayAsync ?? Task.Delay;
    }

    public async Task SyncPendingBatchAsync(CancellationToken ct = default)
    {
        var peeked = await _store.PeekBatchAsync(_options.SyncBatchSize, ct).ConfigureAwait(false);
        if (peeked.Samples.Count == 0)
        {
            return;
        }

        var credentials = await _store.GetCredentialsAsync(ct).ConfigureAwait(false);
        if (credentials is null)
        {
            var enrollRequest = new EnrollRequest(
                string.IsNullOrWhiteSpace(_options.DeviceNameOverride) ? Environment.MachineName : _options.DeviceNameOverride,
                Environment.MachineName,
                Environment.OSVersion.VersionString,
                TelemetryConstants.DefaultAgentVersion);
            var enrollment = await _client.EnrollAsync(enrollRequest, ct).ConfigureAwait(false);
            await _store.SaveCredentialsAsync(enrollment.DeviceId, enrollment.ApiToken, ct).ConfigureAwait(false);
            AgentLogEvents.LogDeviceEnrolled(_logger ?? NullLogger<TelemetrySyncOrchestrator>.Instance, enrollment.DeviceId);
            credentials = (enrollment.DeviceId, enrollment.ApiToken);
        }

        var request = new BatchUploadRequest(credentials.Value.DeviceId, peeked.PendingDroppedCount, peeked.Samples);
        var maxRetries = Math.Max(0, _options.MaxRetriesPerCycle);
        for (var attempt = 0; ; attempt++)
        {
            var outcome = await _client.UploadBatchAsync(request, credentials.Value.ApiToken, ct).ConfigureAwait(false);
            if (outcome.Status == SyncUploadStatus.Success && outcome.Response is not null)
            {
                var response = outcome.Response;
                await _store.ConfirmBatchAsync(response.ConfirmedSampleIds, request.DroppedSamplesCount, ct).ConfigureAwait(false);
                AgentLogEvents.LogBatchSent(
                    _logger ?? NullLogger<TelemetrySyncOrchestrator>.Instance,
                    request.Samples.Count,
                    request.DroppedSamplesCount,
                    request.Samples[0].SequenceNumber,
                    request.Samples[^1].SequenceNumber);
                AgentLogEvents.LogConfirmationReceived(
                    _logger ?? NullLogger<TelemetrySyncOrchestrator>.Instance,
                    response.StoredCount,
                    response.DuplicateCount,
                    response.ConfirmedSampleIds.Count);
                return;
            }

            if (outcome.Status != SyncUploadStatus.TransientFailure || attempt >= maxRetries)
            {
                if (outcome.Status == SyncUploadStatus.PermanentFailure)
                {
                    AgentLogEvents.LogRequestRejected(
                        _logger ?? NullLogger<TelemetrySyncOrchestrator>.Instance,
                        "/api/v1/telemetry/batch",
                        outcome.HttpStatusCode ?? 0,
                        outcome.Reason ?? "The server rejected the batch.");
                }

                return;
            }

            var retryNumber = attempt + 1;
            var delay = _backoff.GetDelay(retryNumber);
            AgentLogEvents.LogRetryScheduled(
                _logger ?? NullLogger<TelemetrySyncOrchestrator>.Instance,
                retryNumber,
                delay,
                outcome.Reason ?? "Transient sync failure.");
            await _delayAsync(delay, ct).ConfigureAwait(false);
        }
    }
}
