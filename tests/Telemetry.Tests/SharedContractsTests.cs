using System;
using System.Text.Json;
using Telemetry.Shared;
using Xunit;

public class SharedContractsTests
{
    [Fact]
    public void TelemetrySample_Serializes_Enums_As_Strings_And_Uses_CamelCase()
    {
        var sample = new TelemetrySample(
            SampleId: Guid.Parse("f2dfd3cc-108c-4d8c-a41b-25e17f0a3a5b"),
            CapturedAtUtc: new DateTimeOffset(2024, 4, 1, 12, 0, 0, TimeSpan.Zero),
            CpuPercent: 42.5m,
            MemoryUsedMb: 8192,
            SessionState: SessionState.NoUser,
            IdleSeconds: null,
            ForegroundProcess: "notepad.exe",
            ForegroundAppName: "Notepad",
            AgentVersion: TelemetryConstants.DefaultAgentVersion,
            SequenceNumber: 17,
            AgentStartedAtUtc: new DateTimeOffset(2024, 4, 1, 10, 0, 0, TimeSpan.Zero),
            QueueDepth: 3);

        var json = JsonSerializer.Serialize(sample, TelemetryJsonOptions.Default);

        Assert.Contains("\"sessionState\":\"NoUser\"", json);
        Assert.Contains("\"foregroundProcess\":\"notepad.exe\"", json);
        Assert.Contains("\"agentVersion\":\"1.0.0\"", json);
        Assert.DoesNotContain("SessionState", json);
    }

    [Fact]
    public void Validator_Accepts_Valid_Active_And_NoUser_Samples()
    {
        var now = DateTimeOffset.UtcNow;

        var activeSample = new TelemetrySample(
            Guid.NewGuid(),
            now.AddMinutes(-2),
            50m,
            16384,
            SessionState.Active,
            12,
            "chrome.exe",
            "Google Chrome",
            TelemetryConstants.DefaultAgentVersion,
            101,
            now.AddHours(-3),
            5);

        var noUserSample = new TelemetrySample(
            Guid.NewGuid(),
            now.AddMinutes(-1),
            12m,
            4096,
            SessionState.NoUser,
            null,
            null,
            null,
            TelemetryConstants.DefaultAgentVersion,
            102,
            now.AddHours(-2),
            0);

        Assert.True(TelemetryValidator.TryValidateSample(activeSample, now, out _));
        Assert.True(TelemetryValidator.TryValidateSample(noUserSample, now, out _));
    }

    [Fact]
    public void Validator_Rejects_ProcessName_With_FilePath_Or_WindowTitle()
    {
        var now = DateTimeOffset.UtcNow;

        var filePathSample = new TelemetrySample(
            Guid.NewGuid(),
            now.AddMinutes(-1),
            10m,
            2048,
            SessionState.Active,
            1,
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            "Google Chrome",
            TelemetryConstants.DefaultAgentVersion,
            201,
            now.AddHours(-1),
            0);

        var windowTitleSample = new TelemetrySample(
            Guid.NewGuid(),
            now.AddMinutes(-1),
            11m,
            2048,
            SessionState.Active,
            1,
            "chrome.exe - Google Search",
            "Google Chrome",
            TelemetryConstants.DefaultAgentVersion,
            202,
            now.AddHours(-1),
            0);

        Assert.False(TelemetryValidator.TryValidateSample(filePathSample, now, out _));
        Assert.False(TelemetryValidator.TryValidateSample(windowTitleSample, now, out _));
    }

    [Fact]
    public void Validator_Rejects_Implausible_Future_Or_Ancient_Timestamps()
    {
        var now = DateTimeOffset.UtcNow;

        var futureSample = new TelemetrySample(
            Guid.NewGuid(),
            now.AddMinutes(10),
            24m,
            4096,
            SessionState.Active,
            0,
            "code.exe",
            "Visual Studio Code",
            TelemetryConstants.DefaultAgentVersion,
            301,
            now.AddHours(-2),
            0);

        var ancientSample = new TelemetrySample(
            Guid.NewGuid(),
            now.AddDays(-45),
            23m,
            4096,
            SessionState.Active,
            0,
            "code.exe",
            "Visual Studio Code",
            TelemetryConstants.DefaultAgentVersion,
            302,
            now.AddDays(-46),
            0);

        Assert.False(TelemetryValidator.TryValidateSample(futureSample, now, out _));
        Assert.False(TelemetryValidator.TryValidateSample(ancientSample, now, out _));
    }

    [Fact]
    public void Validator_Rejects_Out_Of_Range_Cpu_Or_Memory()
    {
        var now = DateTimeOffset.UtcNow;

        var cpuSample = new TelemetrySample(
            Guid.NewGuid(),
            now.AddMinutes(-1),
            101m,
            2048,
            SessionState.Active,
            0,
            "notepad.exe",
            "Notepad",
            TelemetryConstants.DefaultAgentVersion,
            401,
            now.AddHours(-1),
            0);

        var memorySample = new TelemetrySample(
            Guid.NewGuid(),
            now.AddMinutes(-1),
            24m,
            0,
            SessionState.Active,
            0,
            "notepad.exe",
            "Notepad",
            TelemetryConstants.DefaultAgentVersion,
            402,
            now.AddHours(-1),
            0);

        Assert.False(TelemetryValidator.TryValidateSample(cpuSample, now, out _));
        Assert.False(TelemetryValidator.TryValidateSample(memorySample, now, out _));
    }
}
