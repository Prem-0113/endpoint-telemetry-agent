using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Telemetry.Agent.Collectors;

public sealed class MachineMetricsProvider : IMachineMetricsProvider
{
    private const decimal FallbackCpuPercent = 5.0m;
    private const int FallbackMemoryUsedMb = 2048;
    private readonly ILogger<MachineMetricsProvider>? _logger;
    private ulong? _previousIdle;
    private ulong? _previousTotal;

    public MachineMetricsProvider(ILogger<MachineMetricsProvider>? logger = null)
    {
        _logger = logger;
    }

    public (decimal CpuPercent, int MemoryUsedMb) GetMetrics()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return GetWindowsMetrics();
            }

            if (OperatingSystem.IsLinux())
            {
                return GetLinuxMetrics();
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException or OverflowException or
                DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            _logger?.LogWarning(exception, "Machine metrics could not be read; using safe fallback values.");
        }

        return (FallbackCpuPercent, FallbackMemoryUsedMb);
    }

    private (decimal CpuPercent, int MemoryUsedMb) GetWindowsMetrics()
    {
        if (!GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
        {
            throw new IOException("GetSystemTimes failed.");
        }

        var idle = ToUInt64(idleTime);
        var total = checked(ToUInt64(kernelTime) + ToUInt64(userTime));
        var memory = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref memory))
        {
            throw new IOException("GlobalMemoryStatusEx failed.");
        }

        var memoryMb = checked((int)((memory.TotalPhysical - memory.AvailablePhysical) / (1024 * 1024)));
        return (GetCpuPercent(idle, total), Math.Max(1, memoryMb));
    }

    private (decimal CpuPercent, int MemoryUsedMb) GetLinuxMetrics()
    {
        var cpuLine = File.ReadLines("/proc/stat").First(line => line.StartsWith("cpu ", StringComparison.Ordinal));
        var values = cpuLine.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1)
            .Select(value => ulong.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        if (values.Length < 4)
        {
            throw new FormatException("/proc/stat did not contain CPU counters.");
        }

        var idle = checked(values[3] + (values.Length > 4 ? values[4] : 0));
        ulong total = 0;
        foreach (var value in values)
        {
            total = checked(total + value);
        }

        var memoryLines = File.ReadLines("/proc/meminfo").ToArray();
        var totalKb = ReadMemInfoValue(memoryLines, "MemTotal:");
        var availableKb = ReadMemInfoValue(memoryLines, "MemAvailable:");
        var memoryMb = checked((int)((totalKb - availableKb) / 1024));
        return (GetCpuPercent(idle, total), Math.Max(1, memoryMb));
    }

    private decimal GetCpuPercent(ulong idle, ulong total)
    {
        if (_previousIdle is not ulong previousIdle || _previousTotal is not ulong previousTotal)
        {
            _previousIdle = idle;
            _previousTotal = total;
            return FallbackCpuPercent;
        }

        var totalDelta = total - previousTotal;
        var idleDelta = idle - previousIdle;
        _previousIdle = idle;
        _previousTotal = total;
        if (totalDelta == 0)
        {
            return FallbackCpuPercent;
        }

        var usage = 100m * (1m - Math.Min(1m, (decimal)idleDelta / totalDelta));
        return Math.Round(Math.Clamp(usage, 0m, 100m), 1, MidpointRounding.AwayFromZero);
    }

    private static ulong ReadMemInfoValue(IEnumerable<string> lines, string key)
    {
        var line = lines.FirstOrDefault(value => value.StartsWith(key, StringComparison.Ordinal))
            ?? throw new FormatException($"Missing {key} in /proc/meminfo.");
        var number = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1];
        return ulong.Parse(number, CultureInfo.InvariantCulture);
    }

    private static ulong ToUInt64(FileTime value) => ((ulong)value.High << 32) | value.Low;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
