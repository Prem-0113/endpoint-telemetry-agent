using System.Text.Json.Serialization;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Telemetry.Server.Api;
using Telemetry.Server.Services;
using Telemetry.Server.Storage;
using Telemetry.Shared;

var builder = WebApplication.CreateBuilder(args);
var serverOptions = builder.Configuration.GetSection("ServerOptions").Get<ServerOptions>() ?? new ServerOptions();

builder.Services.AddSingleton(serverOptions);
builder.Services.AddSingleton<IServerTelemetryStore, SqliteServerTelemetryStore>();
builder.Services.AddSingleton<IServerTelemetryService, ServerTelemetryService>();
builder.Services.AddSingleton<IDashboardQueryService, DashboardQueryService>();
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNameCaseInsensitive = true;
    options.SerializerOptions.MaxDepth = 16;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.MapType<SessionState>(() => new OpenApiSchema
    {
        Type = "string",
        Enum = Enum.GetNames<SessionState>()
            .Select(value => (IOpenApiAny)new OpenApiString(value))
            .ToList()
    });
});

var app = builder.Build();
await app.Services.GetRequiredService<IServerTelemetryStore>().InitializeAsync();

app.UseExceptionHandler();
app.UseStatusCodePages();
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapTelemetryEndpoints();
app.Run();

public partial class Program;
