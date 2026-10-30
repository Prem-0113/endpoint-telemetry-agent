using System;
using System.Globalization;

namespace Telemetry.Shared;

/// <summary>
/// Shared validation helpers for sample integrity and process name sanitization.
/// </summary>
public static class TelemetryValidator
{
    public static bool TryValidateSample(TelemetrySample sample, DateTimeOffset nowUtc, out string? errorReason)
    {
        if (sample is null)
        {
            errorReason = "Sample is required.";
            return false;
        }

        if (sample.SampleId == Guid.Empty)
        {
            errorReason = "SampleId must not be empty.";
            return false;
        }

        if (sample.SequenceNumber <= 0)
        {
            errorReason = "SequenceNumber must be greater than zero.";
            return false;
        }

        if (sample.CpuPercent < 0m || sample.CpuPercent > 100m)
        {
            errorReason = "CpuPercent must be between 0 and 100.";
            return false;
        }

        if (sample.MemoryUsedMb <= 0)
        {
            errorReason = "MemoryUsedMb must be greater than zero.";
            return false;
        }

        if (sample.QueueDepth < 0)
        {
            errorReason = "QueueDepth cannot be negative.";
            return false;
        }

        if (sample.CapturedAtUtc < nowUtc.Add(TelemetryConstants.MaxPastSampleAge.Negate()) || sample.CapturedAtUtc > nowUtc.Add(TelemetryConstants.MaxFutureClockSkew))
        {
            errorReason = "CapturedAtUtc is outside the acceptable time window.";
            return false;
        }

        if (!Enum.IsDefined(sample.SessionState))
        {
            errorReason = "SessionState is not a defined value.";
            return false;
        }

        if (sample.SessionState == SessionState.NoUser)
        {
            if (sample.IdleSeconds is not null)
            {
                errorReason = "IdleSeconds must be null when SessionState is NoUser.";
                return false;
            }
        }
        else if (sample.IdleSeconds is < 0)
        {
            errorReason = "IdleSeconds must be null or zero or greater for active or locked sessions.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(sample.ForegroundProcess))
        {
            var sanitized = SanitizeProcessName(sample.ForegroundProcess);
            if (sanitized == "Unknown")
            {
                errorReason = "ForegroundProcess must be a safe executable name without file paths or window titles.";
                return false;
            }
        }

        errorReason = null;
        return true;
    }

    public static string SanitizeProcessName(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return "Unknown";
        }

        var candidate = rawName.Trim();

        if (candidate.Contains('/', StringComparison.Ordinal) ||
            candidate.Contains('\\', StringComparison.Ordinal) ||
            candidate.Contains("http://", StringComparison.OrdinalIgnoreCase) ||
            candidate.Contains("https://", StringComparison.OrdinalIgnoreCase) ||
            candidate.Contains(" - ", StringComparison.OrdinalIgnoreCase))
        {
            return "Unknown";
        }

        var cleaned = candidate.Trim('"', '\'', ' ');
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return "Unknown";
        }

        var normalized = cleaned.Trim();
        return normalized.Length > 0 ? normalized : "Unknown";
    }
}
