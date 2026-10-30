using System.Text.Json;
using Telemetry.Shared;

namespace Telemetry.Agent.Collectors;

public static class SessionIpcMessageValidator
{
    public static bool TryValidate(
        ReadOnlySpan<byte> rawMessage,
        DateTimeOffset nowUtc,
        out UserSessionSnapshot? snapshot,
        out string? errorReason)
    {
        snapshot = null;
        if (rawMessage.IsEmpty)
        {
            errorReason = "IPC message must not be empty.";
            return false;
        }

        if (rawMessage.Length > TelemetryConstants.MaxIpcMessageBytes)
        {
            errorReason = $"IPC message exceeds {TelemetryConstants.MaxIpcMessageBytes} bytes.";
            return false;
        }

        try
        {
            snapshot = JsonSerializer.Deserialize<UserSessionSnapshot>(rawMessage, TelemetryJsonOptions.Default);
        }
        catch (JsonException)
        {
            errorReason = "IPC message is not valid session snapshot JSON.";
            return false;
        }

        if (snapshot is null)
        {
            errorReason = "IPC message did not contain a session snapshot.";
            return false;
        }

        if (!Enum.IsDefined(snapshot.SessionState))
        {
            errorReason = "SessionState is not a defined value.";
            return false;
        }

        if (snapshot.CapturedAtUtc < nowUtc.Add(-TelemetryConstants.MaxPastSampleAge) ||
            snapshot.CapturedAtUtc > nowUtc.Add(TelemetryConstants.MaxFutureClockSkew))
        {
            errorReason = "CapturedAtUtc is outside the acceptable time window.";
            return false;
        }

        if (snapshot.SessionState == SessionState.NoUser && snapshot.IdleSeconds is not null)
        {
            errorReason = "IdleSeconds must be null when SessionState is NoUser.";
            return false;
        }

        if ((snapshot.SessionState is SessionState.Active or SessionState.Locked) && snapshot.IdleSeconds is < 0)
        {
            errorReason = "IdleSeconds cannot be negative for an active or locked session.";
            return false;
        }

        if (snapshot.ForegroundProcess is not null &&
            TelemetryValidator.SanitizeProcessName(snapshot.ForegroundProcess) == "Unknown")
        {
            errorReason = "ForegroundProcess must be a safe executable name without file paths or window titles.";
            return false;
        }

        errorReason = null;
        return true;
    }

    public static bool TryValidate(
        string rawMessage,
        DateTimeOffset nowUtc,
        out UserSessionSnapshot? snapshot,
        out string? errorReason)
    {
        ArgumentNullException.ThrowIfNull(rawMessage);
        return TryValidate(System.Text.Encoding.UTF8.GetBytes(rawMessage), nowUtc, out snapshot, out errorReason);
    }
}
