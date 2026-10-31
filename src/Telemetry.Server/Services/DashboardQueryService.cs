using Telemetry.Server.Storage;
using Telemetry.Shared;

namespace Telemetry.Server.Services;

public sealed class DashboardQueryService : IDashboardQueryService
{
    private readonly IServerTelemetryStore _store;
    private readonly ServerOptions _options;

    public DashboardQueryService(IServerTelemetryStore store, ServerOptions options)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<DashboardOverviewDto> GetOverviewAsync(
        string? deviceId,
        string? range,
        DateTimeOffset nowUtc,
        CancellationToken ct = default)
    {
        var now = nowUtc.ToUniversalTime();
        var rangeStart = now - ParseRange(range);
        var allDevices = await _store.GetDevicesAsync(ct).ConfigureAwait(false);
        var devices = string.IsNullOrWhiteSpace(deviceId)
            ? allDevices
            : allDevices.Where(device => device.DeviceId.Equals(deviceId, StringComparison.Ordinal)).ToArray();
        var allowedIds = devices.Select(device => device.DeviceId).ToHashSet(StringComparer.Ordinal);

        var allSamples = await _store.GetSamplesAsync(ct: ct).ConfigureAwait(false);
        var deviceSamples = allSamples.Where(sample => allowedIds.Contains(sample.DeviceId)).ToArray();
        var recentSamples = deviceSamples
            .Where(sample => sample.CapturedAtUtc >= rangeStart)
            .OrderByDescending(sample => sample.CapturedAtUtc)
            .ThenByDescending(sample => sample.SequenceNumber)
            .Take(500)
            .ToArray();

        var deviceStatus = devices.Select(device => new DeviceSummaryDto(
            device.DeviceId,
            device.DeviceName,
            device.EnrolledAtUtc,
            device.LastSeenAtUtc,
            GetStatus(device.LastSeenAtUtc, now),
            device.AgentVersion,
            device.CurrentSessionState)).ToArray();

        var gaps = BuildGaps(deviceSamples, allowedIds, now);
        var rangeSamples = deviceSamples
            .Where(sample => sample.CapturedAtUtc >= rangeStart)
            .OrderBy(sample => sample.CapturedAtUtc)
            .ThenBy(sample => sample.SequenceNumber)
            .ToArray();
        var delivery = new DeliveryStatsDto(
            devices.Sum(device => device.SamplesStored),
            devices.Sum(device => device.DuplicatesIgnored),
            devices.Sum(device => device.SamplesDroppedByCap),
            devices.Sum(device => device.BatchesReceived),
            gaps);
        var rejected = await _store.GetRejectedRequestsAsync(ct).ConfigureAwait(false);
        var filteredRejected = string.IsNullOrWhiteSpace(deviceId)
            ? rejected
            : rejected.Where(request => request.DeviceId is null || request.DeviceId == deviceId).ToArray();

        return new DashboardOverviewDto(
            deviceStatus,
            recentSamples,
            rangeSamples.Select(sample => new MetricPointDto(
                sample.CapturedAtUtc,
                sample.CpuPercent,
                sample.MemoryUsedMb)).ToArray(),
            BuildSessionBands(rangeSamples),
            BuildAppUsage(rangeSamples, now),
            gaps,
            delivery,
            filteredRejected.Take(100).Select(request => new RejectedRequestDto(
                request.Id,
                request.RejectedAtUtc,
                request.Endpoint,
                request.HttpStatus,
                request.Reason,
                request.DeviceId)).ToArray(),
            devices.Select(device => new AgentHealthDto(
                device.DeviceId,
                device.AgentStartedAtUtc,
                device.RestartCount,
                device.CurrentQueueDepth,
                device.LastSyncAtUtc)).ToArray());
    }

    private IReadOnlyList<GapRecordDto> BuildGaps(
        IReadOnlyList<StoredSampleDto> samples,
        IReadOnlySet<string> deviceIds,
        DateTimeOffset detectedAtUtc)
    {
        var gaps = new List<GapRecordDto>();
        foreach (var group in samples.Where(sample => deviceIds.Contains(sample.DeviceId))
                     .GroupBy(sample => sample.DeviceId, StringComparer.Ordinal))
        {
            long? previous = null;
            foreach (var sequence in group.Select(sample => sample.SequenceNumber).Distinct().Order())
            {
                if (previous is long prior && sequence - prior > 1)
                {
                    gaps.Add(new GapRecordDto(
                        group.Key,
                        prior + 1,
                        sequence - 1,
                        sequence - prior - 1,
                        detectedAtUtc));
                }

                previous = sequence;
            }
        }

        return gaps;
    }

    private IReadOnlyList<SessionBandDto> BuildSessionBands(IReadOnlyList<StoredSampleDto> samples)
    {
        var bands = new List<SessionBandDto>();
        if (samples.Count == 0)
        {
            return bands;
        }

        var start = samples[0].CapturedAtUtc;
        var end = start;
        var state = samples[0].SessionState;
        var count = 1;
        for (var index = 1; index < samples.Count; index++)
        {
            var sample = samples[index];
            if (sample.SessionState == state)
            {
                end = sample.CapturedAtUtc;
                count++;
                continue;
            }

            bands.Add(new SessionBandDto(start, end, state, count));
            start = end = sample.CapturedAtUtc;
            state = sample.SessionState;
            count = 1;
        }

        bands.Add(new SessionBandDto(start, end, state, count));
        return bands;
    }

    private AppUsageSummaryDto BuildAppUsage(IReadOnlyList<StoredSampleDto> samples, DateTimeOffset nowUtc)
    {
        var activeSeconds = 0;
        var idleSeconds = 0;
        var lockedSeconds = 0;
        var apps = new Dictionary<string, AppAccumulator>(StringComparer.OrdinalIgnoreCase);
        var intervalSeconds = GetTypicalSampleIntervalSeconds(samples);

        for (var index = 0; index < samples.Count; index++)
        {
            var sample = samples[index];
            var nextCapturedAtUtc = index + 1 < samples.Count ? samples[index + 1].CapturedAtUtc : nowUtc;
            var elapsedSeconds = Math.Min(
                intervalSeconds,
                Math.Max(0, (int)(nextCapturedAtUtc - sample.CapturedAtUtc).TotalSeconds));
            if (elapsedSeconds == 0)
            {
                continue;
            }

            if (sample.SessionState is SessionState.Locked or SessionState.NoUser)
            {
                lockedSeconds += elapsedSeconds;
                continue;
            }

            if ((sample.IdleSeconds ?? 0) > _options.IdleThresholdSeconds)
            {
                idleSeconds += elapsedSeconds;
                continue;
            }

            activeSeconds += elapsedSeconds;
            var process = string.IsNullOrWhiteSpace(sample.ForegroundProcess)
                ? "Unknown"
                : sample.ForegroundProcess.Trim();
            var displayName = string.IsNullOrWhiteSpace(sample.ForegroundAppName)
                ? process
                : sample.ForegroundAppName.Trim();
            if (!apps.TryGetValue(process, out var accumulator))
            {
                accumulator = new AppAccumulator(displayName);
                apps.Add(process, accumulator);
            }

            accumulator.ActiveSeconds += elapsedSeconds;
            accumulator.SampleCount++;
        }

        var topApps = apps.Select(pair => new AppUsageItemDto(
                pair.Key,
                pair.Value.DisplayName,
                pair.Value.ActiveSeconds,
                pair.Value.SampleCount))
            .OrderByDescending(app => app.ActiveSeconds)
            .ThenBy(app => app.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToArray();

        return new AppUsageSummaryDto(
            activeSeconds,
            idleSeconds,
            lockedSeconds,
            _options.IdleThresholdSeconds,
            topApps);
    }

    private static int GetTypicalSampleIntervalSeconds(IReadOnlyList<StoredSampleDto> samples)
    {
        var intervals = new List<int>(Math.Max(0, samples.Count - 1));
        for (var index = 1; index < samples.Count; index++)
        {
            var elapsed = (int)(samples[index].CapturedAtUtc - samples[index - 1].CapturedAtUtc).TotalSeconds;
            if (elapsed > 0)
            {
                intervals.Add(elapsed);
            }
        }

        if (intervals.Count == 0)
        {
            return TelemetryConstants.DefaultSampleIntervalSeconds;
        }

        intervals.Sort();
        return intervals[intervals.Count / 2];
    }

    private static TimeSpan ParseRange(string? range) => range?.Trim().ToLowerInvariant() switch
    {
        "8h" => TimeSpan.FromHours(8),
        "24h" => TimeSpan.FromHours(24),
        _ => TimeSpan.FromHours(1)
    };

    private static string GetStatus(DateTimeOffset? lastSeenAtUtc, DateTimeOffset nowUtc)
    {
        if (lastSeenAtUtc is null)
        {
            return "Offline";
        }

        var age = nowUtc - lastSeenAtUtc.Value;
        if (age <= TelemetryConstants.OnlineThreshold)
        {
            return "Online";
        }

        return age <= TelemetryConstants.StaleThreshold ? "Stale" : "Offline";
    }

    private sealed class AppAccumulator(string displayName)
    {
        public string DisplayName { get; } = displayName;
        public int ActiveSeconds { get; set; }
        public int SampleCount { get; set; }
    }
}
