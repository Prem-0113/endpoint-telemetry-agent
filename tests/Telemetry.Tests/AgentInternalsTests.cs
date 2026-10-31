using System.Text;
using System.Text.Json;
using Telemetry.Agent;
using Telemetry.Agent.Collectors;
using Telemetry.Agent.Configuration;
using Telemetry.Agent.Security;
using Telemetry.Agent.Storage;
using Telemetry.Agent.Sync;
using Telemetry.Shared;

public sealed class AgentInternalsTests
{
    [Fact]
    public void MachineMetricsProvider_Does_Not_Fabricate_Initial_Cpu_Usage()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var provider = new MachineMetricsProvider();
        Assert.Throws<MachineMetricsUnavailableException>(() => provider.GetMetrics());

        Thread.Sleep(1000);
        var (cpuPercent, memoryUsedMb) = provider.GetMetrics();
        Assert.InRange(cpuPercent, 0m, 100m);
        Assert.True(memoryUsedMb > 0);
    }

    [Fact]
    public void Malformed_Messages_Between_Components_Are_Rejected()
    {
        var now = DateTimeOffset.UtcNow;
        var monitor = new UserSessionMonitor(new AgentOptions());
        var invalidMessages = new[]
        {
            Encoding.UTF8.GetBytes("{not-json"),
            Encoding.UTF8.GetBytes(new string('x', TelemetryConstants.MaxIpcMessageBytes + 1)),
            SerializeSnapshot(new UserSessionSnapshot(SessionState.Active, -1, null, null, now)),
            SerializeSnapshot(new UserSessionSnapshot(SessionState.Active, 1, @"C:\Windows\notepad.exe", null, now)),
            SerializeSnapshot(new UserSessionSnapshot(SessionState.Active, 1, "chrome.exe - Private Tab", null, now))
        };

        foreach (var message in invalidMessages)
        {
            var accepted = monitor.ProcessRawIpcMessage(message, now, out var errorReason);
            Assert.False(accepted);
            Assert.False(string.IsNullOrWhiteSpace(errorReason));
        }
    }

    [Fact]
    public async Task Agent_Keeps_Running_After_A_Bad_Message()
    {
        using var directory = new TemporaryDirectory();
        var options = new AgentOptions { DataDirectory = directory.Path };
        var monitor = new UserSessionMonitor(options);
        var now = DateTimeOffset.UtcNow;

        var malformedAccepted = monitor.ProcessRawIpcMessage(Encoding.UTF8.GetBytes("{bad"), now, out _);
        Assert.False(malformedAccepted);

        var validSnapshot = new UserSessionSnapshot(SessionState.Active, 3, "notepad.exe", "Notepad", now);
        Assert.True(monitor.ProcessRawIpcMessage(SerializeSnapshot(validSnapshot), now, out _));
        var collectedSnapshot = await monitor.GetSnapshotAsync(now);
        Assert.Equal(SessionState.Active, collectedSnapshot.SessionState);

        await using var store = new SqliteOfflineSampleStore(options, new AesGcmDataProtectionService(options));
        await store.InitializeAsync();
        var client = new FakeSyncClient();
        var sync = new TelemetrySyncOrchestrator(
            options,
            store,
            client,
            new ExponentialBackoffPolicy(options, disableJitter: true));
        var worker = new Worker(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Worker>.Instance,
            options,
            store,
            new FixedMachineMetricsProvider(),
            monitor,
            sync);

        var sample = await worker.CaptureAndStoreSampleAsync();

        Assert.Equal(SessionState.Active, sample.SessionState);
        Assert.Equal(1, await store.GetQueueDepthAsync());
    }

    [Fact]
    public async Task UserSessionMonitor_Uses_Fresh_Ipc_And_Ignores_Stale_Ipc()
    {
        using var directory = new TemporaryDirectory();
        var options = new AgentOptions { DataDirectory = directory.Path };
        var monitor = new UserSessionMonitor(options);
        var now = DateTimeOffset.UtcNow;
        var freshSnapshot = new UserSessionSnapshot(SessionState.Locked, 0, null, null, now);
        var ipcPath = System.IO.Path.Combine(directory.Path, "session-ipc.json");
        await File.WriteAllBytesAsync(ipcPath, SerializeSnapshot(freshSnapshot));

        var collected = await monitor.GetSnapshotAsync(now);
        Assert.Equal(SessionState.Locked, collected.SessionState);

        File.SetLastWriteTimeUtc(ipcPath, DateTime.UtcNow.AddMinutes(-3));
        collected = await monitor.GetSnapshotAsync(DateTimeOffset.UtcNow);
        Assert.NotEqual(freshSnapshot.CapturedAtUtc, collected.CapturedAtUtc);
        Assert.True(collected.CapturedAtUtc >= DateTimeOffset.UtcNow.AddSeconds(-2));
    }

    private static byte[] SerializeSnapshot(UserSessionSnapshot snapshot) =>
        JsonSerializer.SerializeToUtf8Bytes(snapshot, TelemetryJsonOptions.Default);

    private sealed class FixedMachineMetricsProvider : IMachineMetricsProvider
    {
        public (decimal CpuPercent, int MemoryUsedMb) GetMetrics() => (12.5m, 4096);
    }

    private sealed class FakeSyncClient : ITelemetrySyncClient
    {
        public Task<EnrollResponse> EnrollAsync(EnrollRequest request, CancellationToken ct) =>
            Task.FromResult(new EnrollResponse("device-1", "token", DateTimeOffset.UtcNow));

        public Task<SyncUploadOutcome> UploadBatchAsync(
            BatchUploadRequest request,
            string apiToken,
            CancellationToken ct) =>
            Task.FromResult(SyncUploadOutcome.Transient("not used by this test"));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"telemetry-internals-tests-{Guid.NewGuid():N}");

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
