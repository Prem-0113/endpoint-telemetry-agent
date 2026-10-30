using System.Text.Json;
using System.Text.Json.Serialization;

namespace Telemetry.Shared;

/// <summary>
/// Shared JSON settings used across agent IPC and server API contracts.
/// </summary>
public static class TelemetryJsonOptions
{
    public static JsonSerializerOptions Default { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        MaxDepth = 16,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    static TelemetryJsonOptions()
    {
        Default.Converters.Add(new JsonStringEnumConverter());
    }
}
