using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Telemetry.Agent.Configuration;
using Telemetry.Agent.Security;
using Telemetry.Agent.Storage;
using Telemetry.Agent.Sync;
using Telemetry.Server.Api;
using Telemetry.Server.Services;
using Telemetry.Server.Storage;
using Telemetry.Shared;

public sealed class ServerAndDashboardTests
{
    [Fact]
    public async Task Server_Repeated_Batch_Is_Stored_Once_And_Increments_DuplicatesIgnored()
    {
        using var directory = new TemporaryDirectory();
        await using var harness = await TestHarness.CreateAsync(directory.Path);
        var (device, _) = await harness.EnrollAsync();
        var request = new BatchUploadRequest(device.DeviceId, 0, MakeSamples(harness.Now, [1, 2, 3]));

        var first = await harness.Store.IngestBatchAsync(device.DeviceId, request, harness.Now);
        var repeated = await harness.Store.IngestBatchAsync(device.DeviceId, request, harness.Now);
        var overview = await harness.Dashboard.GetOverviewAsync(null, "1h", harness.Now);

        Assert.Equal(3, first.StoredCount);
        Assert.Equal(3, repeated.DuplicateCount);
        Assert.Equal(3, overview.DeliveryStats.SamplesStored);
        Assert.Equal(3, overview.DeliveryStats.DuplicatesIgnored);
        Assert.Equal(2, overview.DeliveryStats.BatchesReceived);
    }

    [Fact]
    public async Task Server_Detects_Missing_Sample_Sequence_Gaps_And_Closes_Gap_On_Backfill()
    {
        using var directory = new TemporaryDirectory();
        await using var harness = await TestHarness.CreateAsync(directory.Path);
        var (device, _) = await harness.EnrollAsync();
        await harness.IngestAsync(device.DeviceId, [1, 2, 5]);

        var overview = await harness.Dashboard.GetOverviewAsync(device.DeviceId, "1h", harness.Now);
        var gap = Assert.Single(overview.MissingSampleGaps);
        Assert.Equal(3, gap.FromSequence);
        Assert.Equal(4, gap.ToSequence);
        Assert.Equal(2, gap.MissingCount);

        await harness.IngestAsync(device.DeviceId, [3, 4]);

        overview = await harness.Dashboard.GetOverviewAsync(device.DeviceId, "1h", harness.Now);
        Assert.Empty(overview.MissingSampleGaps);
    }

    [Fact]
    public async Task Server_Rejects_Invalid_Unauthenticated_And_Oversized_Requests_And_Records_Reasons()
    {
        using var directory = new TemporaryDirectory();
        await using var api = await ApiHarness.StartAsync(directory.Path, maxBodyBytes: 2048);
        var enrollment = await api.Store.EnrollDeviceAsync(
            new EnrollRequest("Test Device", "machine-1", "Linux", "1.0.0"),
            DateTimeOffset.UtcNow);
        var deviceId = enrollment.DeviceId;
        var token = enrollment.ApiToken;

        using var unauthenticated = await api.Client.PostAsync(
            "/api/v1/telemetry/batch",
            JsonContent(new BatchUploadRequest(deviceId, 0, MakeSamples(DateTimeOffset.UtcNow, [1]))));
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

        var invalidSample = MakeSamples(DateTimeOffset.UtcNow, [2])[0] with { CpuPercent = 101m };
        using var invalidRequest = CreateRequest(
            deviceId,
            token,
            new BatchUploadRequest(deviceId, 0, [invalidSample]));
        using var invalidResponse = await api.Client.SendAsync(invalidRequest);
        Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);

        using var oversizedRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/telemetry/batch")
        {
            Content = new StringContent(new string('x', 4096), Encoding.UTF8, "application/json")
        };
        using var oversizedResponse = await api.Client.SendAsync(oversizedRequest);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversizedResponse.StatusCode);

        Assert.Empty(await api.Store.GetSamplesAsync());
        var rejected = await api.Store.GetRejectedRequestsAsync();
        Assert.Equal(3, rejected.Count);
        Assert.Equal(
            new[] { 413, 400, 401 },
            rejected.OrderByDescending(item => item.Id).Select(item => item.HttpStatus));
        Assert.All(rejected, item => Assert.False(string.IsNullOrWhiteSpace(item.Reason)));
    }

    [Fact]
    public async Task Dashboard_AppTime_Excludes_Locked_NoUser_And_Idle_Samples_Above_Threshold()
    {
        using var directory = new TemporaryDirectory();
        await using var harness = await TestHarness.CreateAsync(directory.Path);
        var (device, _) = await harness.EnrollAsync();
        var samples = new[]
        {
            MakeSample(harness.Now, 1, SessionState.Active, 15, "chrome.exe"),
            MakeSample(harness.Now, 2, SessionState.Active, 360, "chrome.exe"),
            MakeSample(harness.Now, 3, SessionState.Locked, 120, "chrome.exe"),
            MakeSample(harness.Now, 4, SessionState.NoUser, null, "chrome.exe")
        };
        await harness.Store.IngestBatchAsync(device.DeviceId, new BatchUploadRequest(device.DeviceId, 0, samples), harness.Now);

        var overview = await harness.Dashboard.GetOverviewAsync(device.DeviceId, "1h", harness.Now);
        var chrome = Assert.Single(overview.AppUsageSummary.TopApps);
        Assert.Equal("chrome.exe", chrome.ProcessName);
        Assert.Equal(60, chrome.ActiveSeconds);
        Assert.Equal(60, overview.AppUsageSummary.TotalActiveSeconds);
        Assert.Equal(60, overview.AppUsageSummary.TotalIdleSeconds);
        Assert.Equal(120, overview.AppUsageSummary.TotalLockedSeconds);
    }

    [Fact]
    public async Task Dashboard_Duplicate_And_Backfilled_Samples_Count_Once_In_AppTime()
    {
        using var directory = new TemporaryDirectory();
        await using var harness = await TestHarness.CreateAsync(directory.Path);
        var (device, _) = await harness.EnrollAsync();
        var firstAndThird = new BatchUploadRequest(
            device.DeviceId,
            0,
            MakeSamples(harness.Now, [1, 3], processName: "code.exe"));
        await harness.Store.IngestBatchAsync(device.DeviceId, firstAndThird, harness.Now);
        await harness.Store.IngestBatchAsync(device.DeviceId, firstAndThird with { Samples = [firstAndThird.Samples[0]] }, harness.Now);
        await harness.IngestAsync(device.DeviceId, [2], processName: "code.exe");

        var overview = await harness.Dashboard.GetOverviewAsync(device.DeviceId, "1h", harness.Now);
        var code = Assert.Single(overview.AppUsageSummary.TopApps);
        Assert.Equal("code.exe", code.ProcessName);
        Assert.Equal(180, code.ActiveSeconds);
        Assert.Equal(3, code.SampleCount);
    }

    [Fact]
    public async Task EndToEnd_Agent_SyncOrchestrator_Uploads_To_Server_Store_Cleanly()
    {
        using var directory = new TemporaryDirectory();
        var serverDirectory = Path.Combine(directory.Path, "server");
        var agentDirectory = Path.Combine(directory.Path, "agent");
        Directory.CreateDirectory(serverDirectory);
        Directory.CreateDirectory(agentDirectory);
        await using var server = await ApiHarness.StartAsync(serverDirectory, TelemetryConstants.MaxBatchRequestBytes);

        var agentOptions = new AgentOptions { DataDirectory = agentDirectory, SyncBatchSize = 10 };
        await using var agentStore = new SqliteOfflineSampleStore(agentOptions, new AesGcmDataProtectionService(agentOptions));
        await agentStore.InitializeAsync();
        await EnqueueAgentSamplesAsync(agentStore, DateTimeOffset.UtcNow, 2);
        var client = new HttpTelemetrySyncClient(server.Client);
        var orchestrator = new TelemetrySyncOrchestrator(
            agentOptions,
            agentStore,
            client,
            new ExponentialBackoffPolicy(agentOptions, disableJitter: true));

        await orchestrator.SyncPendingBatchAsync();

        var dashboard = new DashboardQueryService(server.Store, new ServerOptions { DataDirectory = serverDirectory });
        var overview = await dashboard.GetOverviewAsync(null, "1h", DateTimeOffset.UtcNow);
        Assert.Equal(0, await agentStore.GetQueueDepthAsync());
        Assert.Equal(2, overview.DeliveryStats.SamplesStored);
    }

    private static StringContent JsonContent<T>(T value) =>
        new(JsonSerializer.Serialize(value, TelemetryJsonOptions.Default), Encoding.UTF8, "application/json");

    private static HttpRequestMessage CreateRequest(string deviceId, string token, BatchUploadRequest batch)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/telemetry/batch")
        {
            Content = JsonContent(batch)
        };
        request.Headers.Add("X-Device-Id", deviceId);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static TelemetrySample[] MakeSamples(
        DateTimeOffset now,
        long[] sequences,
        string processName = "notepad.exe") =>
        sequences.Select(sequence => MakeSample(now, sequence, SessionState.Active, 5, processName)).ToArray();

    private static TelemetrySample MakeSample(
        DateTimeOffset now,
        long sequence,
        SessionState sessionState,
        int? idleSeconds,
        string? processName)
    {
        var capturedAt = now.AddSeconds(-sequence);
        return new TelemetrySample(
            Guid.NewGuid(),
            capturedAt,
            21.5m,
            4096,
            sessionState,
            idleSeconds,
            processName,
            processName is null ? null : "Test App",
            TelemetryConstants.DefaultAgentVersion,
            sequence,
            now.AddHours(-2),
            1);
    }

    private static async Task EnqueueAgentSamplesAsync(
        SqliteOfflineSampleStore store,
        DateTimeOffset now,
        int count)
    {
        for (var index = 1; index <= count; index++)
        {
            var sample = MakeSample(now, await store.AllocateNextSequenceNumberAsync(), SessionState.Active, 5, "agent.exe");
            await store.EnqueueSampleAsync(sample);
        }
    }

    private sealed class TestHarness : IAsyncDisposable
    {
        public SqliteServerTelemetryStore Store { get; }
        public DashboardQueryService Dashboard { get; }
        public DateTimeOffset Now { get; } = DateTimeOffset.UtcNow;

        private TestHarness(TemporaryDirectory directory)
        {
            var options = new ServerOptions { DataDirectory = directory.Path };
            Store = new SqliteServerTelemetryStore(options);
            Dashboard = new DashboardQueryService(Store, options);
        }

        public static async Task<TestHarness> CreateAsync(string directory)
        {
            var harness = new TestHarness(new TemporaryDirectory(directory));
            await harness.Store.InitializeAsync();
            return harness;
        }

        public async Task<(EnrollResponse Device, string Token)> EnrollAsync()
        {
            var device = await Store.EnrollDeviceAsync(
                new EnrollRequest("Test Device", "machine-" + Guid.NewGuid(), "Linux", "1.0.0"),
                Now);
            return (device, device.ApiToken);
        }

        public async Task IngestAsync(string deviceId, long[] sequences, string processName = "notepad.exe")
        {
            await Store.IngestBatchAsync(
                deviceId,
                new BatchUploadRequest(deviceId, 0, MakeSamples(Now, sequences, processName)),
                Now);
        }

        public async ValueTask DisposeAsync()
        {
            await Store.DisposeAsync();
        }
    }

    private sealed class ApiHarness : IAsyncDisposable
    {
        private readonly WebApplication _app;
        public HttpClient Client { get; }
        public SqliteServerTelemetryStore Store { get; }

        private ApiHarness(WebApplication app, HttpClient client, SqliteServerTelemetryStore store)
        {
            _app = app;
            Client = client;
            Store = store;
        }

        public static async Task<ApiHarness> StartAsync(string directory, int maxBodyBytes)
        {
            var options = new ServerOptions { DataDirectory = directory, MaxBatchRequestBytes = maxBodyBytes };
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(options);
            builder.Services.AddSingleton<SqliteServerTelemetryStore>();
            builder.Services.AddSingleton<IServerTelemetryStore>(services =>
                services.GetRequiredService<SqliteServerTelemetryStore>());
            builder.Services.AddSingleton<IServerTelemetryService, ServerTelemetryService>();
            builder.Services.AddSingleton<IDashboardQueryService, DashboardQueryService>();
            builder.Services.AddProblemDetails();
            builder.Services.ConfigureHttpJsonOptions(json =>
                json.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

            var app = builder.Build();
            var store = app.Services.GetRequiredService<SqliteServerTelemetryStore>();
            await store.InitializeAsync();
            app.MapTelemetryEndpoints();
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new ApiHarness(app, new HttpClient { BaseAddress = new Uri(address) }, store);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
            await Store.DisposeAsync();
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; }

        public TemporaryDirectory() : this(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"telemetry-server-tests-{Guid.NewGuid():N}"))
        {
        }

        public TemporaryDirectory(string path)
        {
            Path = path;
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
