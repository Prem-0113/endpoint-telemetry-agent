using System.Text.Json.Serialization;

namespace Telemetry.Shared;

/// <summary>
/// Represents the user session state observed on the host. It is serialized as a string value for compact JSON payloads.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SessionState
{
    Active,
    Locked,
    NoUser
}
