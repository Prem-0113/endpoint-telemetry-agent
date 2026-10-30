using Telemetry.Agent;
using Telemetry.Agent.Collectors;
using Telemetry.Agent.Configuration;
using Telemetry.Agent.Security;
using Telemetry.Agent.Storage;
using Telemetry.Agent.Sync;

var sessionProbeMode = args.Contains("--session-probe", StringComparer.OrdinalIgnoreCase);
var hostArguments = args.Where(argument => !string.Equals(
    argument,
    "--session-probe",
    StringComparison.OrdinalIgnoreCase)).ToArray();
var builder = Host.CreateApplicationBuilder(hostArguments);

var agentOptions = builder.Configuration.GetSection("AgentOptions").Get<AgentOptions>() ?? new AgentOptions();
if (sessionProbeMode)
{
    await UserSessionMonitor.RunSessionProbeAsync(
        agentOptions,
        LoggerFactory.Create(logging => logging.AddConsole()).CreateLogger("SessionProbe"));
    return;
}

builder.Services.AddWindowsService(options => options.ServiceName = "EndpointTelemetryAgent");
builder.Services.AddSingleton(agentOptions);
builder.Services.AddSingleton<IDataProtectionService, AesGcmDataProtectionService>();
builder.Services.AddSingleton<IOfflineSampleStore, SqliteOfflineSampleStore>();
builder.Services.AddSingleton<IMachineMetricsProvider, MachineMetricsProvider>();
builder.Services.AddSingleton<IUserSessionProvider, UserSessionMonitor>();
builder.Services.AddSingleton<ExponentialBackoffPolicy>();
builder.Services.AddSingleton<ITelemetrySyncClient>(services =>
{
    var options = services.GetRequiredService<AgentOptions>();
    var client = new HttpClient
    {
        BaseAddress = new Uri(options.ServerBaseUrl, UriKind.Absolute),
        Timeout = TimeSpan.FromSeconds(Math.Max(1, options.HttpTimeoutSeconds))
    };
    return new HttpTelemetrySyncClient(client, services.GetRequiredService<ILogger<HttpTelemetrySyncClient>>());
});
builder.Services.AddSingleton<TelemetrySyncOrchestrator>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
