using Telemetry.Agent.Configuration;
using Telemetry.Agent.Security;
using Telemetry.Agent.Storage;
using Telemetry.Agent.Sync;
using Telemetry.Shared;

public sealed class SyncServiceTests
{
    [Fact]
    public async Task Sync_Retries_With_Exponential_Backoff_On_Transient_Failure()
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path);
        options.RetryBaseDelayMs = 10;
        options.RetryMaxDelayMs = 100;
        var delayValues = new List<TimeSpan>();
        await using var store = CreateStore(options);
        await store.InitializeAsync();
        await store.SaveCredentialsAsync("device-1", "test-token");
        await EnqueueSamplesAsync(store, 2);

        var client = new FakeSyncClient();
        client.UploadResults.Enqueue(_ => SyncUploadOutcome.Transient("temporary failure"));
        client.UploadResults.Enqueue(_ => SyncUploadOutcome.Transient("temporary failure"));
        client.UploadResults.Enqueue(request => SuccessFor(request));
        var orchestrator = CreateOrchestrator(options, store, client, delayValues);

        await orchestrator.SyncPendingBatchAsync();

        Assert.Equal(3, client.UploadRequests.Count);
        Assert.Equal(2, delayValues.Count);
        Assert.True(delayValues[1] > delayValues[0]);
        Assert.Equal(0, await store.GetQueueDepthAsync());
    }

    [Fact]
    public async Task Sync_Retry_Never_Creates_Duplicate_Or_New_SampleIds()
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path);
        await using var store = CreateStore(options);
        await store.InitializeAsync();
        await store.SaveCredentialsAsync("device-1", "test-token");
        await EnqueueSamplesAsync(store, 2);

        var client = new FakeSyncClient();
        client.UploadResults.Enqueue(_ => SyncUploadOutcome.Transient("temporary failure"));
        client.UploadResults.Enqueue(_ => SyncUploadOutcome.Transient("temporary failure"));
        client.UploadResults.Enqueue(request => SuccessFor(request));
        var orchestrator = CreateOrchestrator(options, store, client, new List<TimeSpan>());

        await orchestrator.SyncPendingBatchAsync();

        var firstRequest = client.UploadRequests[0];
        Assert.Equal(3, client.UploadRequests.Count);
        foreach (var request in client.UploadRequests.Skip(1))
        {
            Assert.Equal(
                firstRequest.Samples.Select(sample => (sample.SampleId, sample.SequenceNumber)),
                request.Samples.Select(sample => (sample.SampleId, sample.SequenceNumber)));
        }
    }

    [Fact]
    public async Task Sync_Removes_Samples_Locally_Only_After_Server_Confirmation()
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path);
        options.MaxRetriesPerCycle = 0;
        await using var store = CreateStore(options);
        await store.InitializeAsync();
        await store.SaveCredentialsAsync("device-1", "test-token");
        await EnqueueSamplesAsync(store, 3);

        var client = new FakeSyncClient();
        client.UploadResults.Enqueue(_ => SyncUploadOutcome.Transient("network unavailable"));
        var orchestrator = CreateOrchestrator(options, store, client, new List<TimeSpan>());
        await orchestrator.SyncPendingBatchAsync();
        Assert.Equal(3, await store.GetQueueDepthAsync());

        client.UploadResults.Enqueue(request => SuccessFor(request));
        await orchestrator.SyncPendingBatchAsync();

        Assert.Equal(0, await store.GetQueueDepthAsync());
    }

    private static AgentOptions CreateOptions(string directory) => new() { DataDirectory = directory };

    private static SqliteOfflineSampleStore CreateStore(AgentOptions options) =>
        new(options, new AesGcmDataProtectionService(options));

    private static async Task EnqueueSamplesAsync(SqliteOfflineSampleStore store, int count)
    {
        var now = DateTimeOffset.UtcNow;
        for (var index = 1; index <= count; index++)
        {
            await store.EnqueueSampleAsync(new TelemetrySample(
                Guid.NewGuid(),
                now,
                17.5m,
                2048,
                SessionState.Active,
                10,
                "editor.exe",
                "Editor",
                TelemetryConstants.DefaultAgentVersion,
                await store.AllocateNextSequenceNumberAsync(),
                now.AddHours(-1),
                index));
        }
    }

    private static SyncUploadOutcome SuccessFor(BatchUploadRequest request) =>
        SyncUploadOutcome.Succeeded(new BatchUploadResponse(
            request.Samples.Count,
            0,
            request.Samples.Select(sample => sample.SampleId).ToArray(),
            DateTimeOffset.UtcNow));

    private static TelemetrySyncOrchestrator CreateOrchestrator(
        AgentOptions options,
        IOfflineSampleStore store,
        ITelemetrySyncClient client,
        List<TimeSpan> delays) =>
        new(
            options,
            store,
            client,
            new ExponentialBackoffPolicy(options, disableJitter: true),
            delayAsync: (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

    private sealed class FakeSyncClient : ITelemetrySyncClient
    {
        public Queue<Func<BatchUploadRequest, SyncUploadOutcome>> UploadResults { get; } = new();
        public List<BatchUploadRequest> UploadRequests { get; } = new();

        public Task<EnrollResponse> EnrollAsync(EnrollRequest request, CancellationToken ct) =>
            Task.FromResult(new EnrollResponse("device-1", "test-token", DateTimeOffset.UtcNow));

        public Task<SyncUploadOutcome> UploadBatchAsync(
            BatchUploadRequest request,
            string apiToken,
            CancellationToken ct)
        {
            UploadRequests.Add(request);
            return Task.FromResult(UploadResults.Dequeue()(request));
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"telemetry-sync-tests-{Guid.NewGuid():N}");

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
