using System.Text;
using Telemetry.Agent.Configuration;
using Telemetry.Agent.Security;
using Telemetry.Agent.Storage;
using Telemetry.Shared;
using Xunit;

public sealed class OfflineStorageTests
{
    [Fact]
    public async Task Samples_Persist_Across_Process_Restart_And_RoundTrip_All_Fields()
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path);
        var expected = Enumerable.Range(1, 3).Select(index => CreateSample(index)).ToArray();

        await using (var store = CreateStore(options))
        {
            await store.InitializeAsync();
            foreach (var sample in expected)
            {
                await store.EnqueueSampleAsync(sample);
            }
        }

        await using var reopenedStore = CreateStore(options);
        var result = await reopenedStore.PeekBatchAsync(10);

        Assert.Equal(expected, result.Samples);
        Assert.Equal(0, result.PendingDroppedCount);
    }

    [Fact]
    public async Task SequenceNumber_Continues_After_Restart_And_Is_Never_Reused()
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path);

        await using (var firstStore = CreateStore(options))
        {
            Assert.Equal(1, await firstStore.AllocateNextSequenceNumberAsync());
            Assert.Equal(2, await firstStore.AllocateNextSequenceNumberAsync());
            Assert.Equal(3, await firstStore.AllocateNextSequenceNumberAsync());
        }

        await using var reopenedStore = CreateStore(options);
        Assert.Equal(4, await reopenedStore.AllocateNextSequenceNumberAsync());
    }

    [Fact]
    public async Task Stored_Samples_And_Credentials_Are_Encrypted_At_Rest_On_Disk()
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path);
        const string privateProcessName = "top_secret_app.exe";
        const string privateToken = "super_secret_bearer_token_999";

        await using (var store = CreateStore(options))
        {
            await store.EnqueueSampleAsync(CreateSample(1, privateProcessName));
            await store.SaveCredentialsAsync("device-private-id", privateToken);
        }

        var databaseAndWalBytes = Directory
            .EnumerateFiles(directory.Path, "agent-telemetry.db*")
            .SelectMany(File.ReadAllBytes)
            .ToArray();
        var diskContents = Encoding.UTF8.GetString(databaseAndWalBytes);

        Assert.DoesNotContain(privateProcessName, diskContents, StringComparison.Ordinal);
        Assert.DoesNotContain(privateToken, diskContents, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StorageCap_Drops_Oldest_Samples_When_Full_And_Counts_Drops()
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path);
        options.MaxOfflineStorageBytes = 2_000;

        await using var store = CreateStore(options);
        for (var sequence = 1; sequence <= 6; sequence++)
        {
            await store.EnqueueSampleAsync(CreateSample(sequence));
        }

        var result = await store.PeekBatchAsync(10);

        Assert.Equal(new long[] { 4, 5, 6 }, result.Samples.Select(sample => sample.SequenceNumber));
        Assert.Equal(3, await store.GetPendingDroppedCountAsync());
    }

    [Fact]
    public async Task Samples_Are_Removed_Only_After_Server_Confirmation()
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path);
        var samples = Enumerable.Range(1, 4).Select(index => CreateSample(index)).ToArray();

        await using var store = CreateStore(options);
        foreach (var sample in samples)
        {
            await store.EnqueueSampleAsync(sample);
        }

        var peeked = await store.PeekBatchAsync(10);
        Assert.Equal(4, peeked.Samples.Count);
        Assert.Equal(4, await store.GetQueueDepthAsync());

        await store.ConfirmBatchAsync(samples.Take(2).Select(sample => sample.SampleId).ToArray(), 0);

        var remaining = await store.PeekBatchAsync(10);
        Assert.Equal(2, await store.GetQueueDepthAsync());
        Assert.Equal(new long[] { 3, 4 }, remaining.Samples.Select(sample => sample.SequenceNumber));
    }

    [Fact]
    public async Task RestartCount_Increments_Persistently_Across_Startups()
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path);

        for (var expectedCount = 1; expectedCount <= 3; expectedCount++)
        {
            await using var store = CreateStore(options);
            Assert.Equal(expectedCount, await store.RecordAgentStartupAsync());
        }
    }

    private static AgentOptions CreateOptions(string directory) => new()
    {
        DataDirectory = directory
    };

    private static SqliteOfflineSampleStore CreateStore(AgentOptions options) =>
        new(options, new AesGcmDataProtectionService(options));

    private static TelemetrySample CreateSample(long sequenceNumber, string? processName = "notepad.exe") =>
        new(
            SampleId: Guid.Parse($"00000000-0000-0000-0000-{sequenceNumber:000000000000}"),
            CapturedAtUtc: new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero).AddSeconds(sequenceNumber),
            CpuPercent: 17.25m + sequenceNumber,
            MemoryUsedMb: 2048 + (int)sequenceNumber,
            SessionState: SessionState.Active,
            IdleSeconds: 30 + (int)sequenceNumber,
            ForegroundProcess: processName,
            ForegroundAppName: "Notepad",
            AgentVersion: TelemetryConstants.DefaultAgentVersion,
            SequenceNumber: sequenceNumber,
            AgentStartedAtUtc: new DateTimeOffset(2026, 10, 29, 11, 0, 0, TimeSpan.Zero),
            QueueDepth: (int)sequenceNumber);

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"telemetry-tests-{Guid.NewGuid():N}");

        public TemporaryDirectory()
        {
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
