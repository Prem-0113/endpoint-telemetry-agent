using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Telemetry.Agent.Configuration;
using Telemetry.Agent.Logging;
using Telemetry.Shared;

namespace Telemetry.Agent.Collectors;

public sealed class UserSessionMonitor : IUserSessionProvider
{
    private static readonly TimeSpan IpcMaximumAge = TimeSpan.FromMinutes(2);
    private static readonly (string[] Names, string ProcessName, string DisplayName)[] LinuxApplications =
    [
        (["code", "code-oss", "codium"], "code.exe", "Visual Studio Code"),
        (["firefox"], "firefox.exe", "Mozilla Firefox"),
        (["chrome", "google-chrome", "chromium", "chromium-browser"], "chrome.exe", "Google Chrome"),
        (["gnome-terminal-server", "gnome-terminal", "ptyxis", "konsole", "xfce4-terminal", "x-terminal-emulator"], "wt.exe", "Terminal"),
        (["bash", "zsh", "fish"], "wt.exe", "Terminal"),
        (["slack"], "slack.exe", "Slack"),
        (["teams", "teams-for-linux"], "teams.exe", "Microsoft Teams"),
        (["thunderbird"], "thunderbird.exe", "Mozilla Thunderbird"),
        (["idea", "idea64"], "idea64.exe", "IntelliJ IDEA"),
        (["pycharm", "pycharm64"], "pycharm64.exe", "PyCharm")
    ];
    private readonly AgentOptions _options;
    private readonly ILogger<UserSessionMonitor>? _logger;
    private UserSessionSnapshot _latestSnapshot = new(SessionState.NoUser, null, null, null, DateTimeOffset.UtcNow);
    private bool _hasIpcSnapshot;

    public UserSessionMonitor(AgentOptions options, ILogger<UserSessionMonitor>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
    }

    public DateTimeOffset? LastProbeFaultUtc { get; private set; }

    public bool ProcessRawIpcMessage(ReadOnlySpan<byte> rawMessage, DateTimeOffset nowUtc, out string? errorReason)
    {
        if (!SessionIpcMessageValidator.TryValidate(rawMessage, nowUtc, out var snapshot, out errorReason))
        {
            AgentLogEvents.LogIpcMessageRejected(
                _logger ?? NullLogger<UserSessionMonitor>.Instance,
                errorReason ?? "Unknown validation error.");
            return false;
        }

        _latestSnapshot = snapshot!;
        _hasIpcSnapshot = true;
        return true;
    }

    public async Task<UserSessionSnapshot> GetSnapshotAsync(DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        try
        {
            var ipcPath = Path.Combine(_options.DataDirectory, "session-ipc.json");
            if (File.Exists(ipcPath))
            {
                var fileAge = DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(ipcPath);
                if (fileAge >= TimeSpan.Zero && fileAge <= IpcMaximumAge)
                {
                    if (new FileInfo(ipcPath).Length <= TelemetryConstants.MaxIpcMessageBytes)
                    {
                        var bytes = await File.ReadAllBytesAsync(ipcPath, ct).ConfigureAwait(false);
                        if (ProcessRawIpcMessage(bytes, nowUtc, out _))
                        {
                            LastProbeFaultUtc = null;
                            return _latestSnapshot;
                        }
                    }
                    else
                    {
                        ProcessRawIpcMessage(
                            new byte[TelemetryConstants.MaxIpcMessageBytes + 1],
                            nowUtc,
                            out _);
                    }
                }
            }

            if (OperatingSystem.IsWindows())
            {
                _latestSnapshot = Environment.UserInteractive
                    ? ProbeInteractiveWindowsSession(nowUtc)
                    : ProbeWindowsServiceSession(nowUtc);
            }
            else if (OperatingSystem.IsLinux())
            {
                _latestSnapshot = ProbeLinuxDesktopSession(nowUtc);
            }
            else if (!_hasIpcSnapshot)
            {
                _latestSnapshot = new UserSessionSnapshot(SessionState.NoUser, null, null, null, nowUtc);
            }

            LastProbeFaultUtc = null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LastProbeFaultUtc = nowUtc;
            _logger?.LogWarning(exception, "User session probe failed; retaining the latest valid snapshot and retrying next cycle.");
        }

        return _latestSnapshot;
    }

    public static async Task RunSessionProbeAsync(
        AgentOptions options,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The Windows session probe can run only on Windows.");
        }

        var monitor = new UserSessionMonitor(options);
        var ipcPath = Path.GetFullPath(Path.Combine(options.DataDirectory, "session-ipc.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(ipcPath)!);
        logger?.LogInformation("Interactive session probe started; publishing validated snapshots every 5 seconds.");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                var snapshot = monitor.ProbeInteractiveWindowsSession(now);
                var payload = JsonSerializer.SerializeToUtf8Bytes(snapshot, TelemetryJsonOptions.Default);
                if (!SessionIpcMessageValidator.TryValidate(payload, now, out _, out var validationError))
                {
                    logger?.LogWarning("Interactive session snapshot rejected before publish: {Reason}", validationError);
                    await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                    continue;
                }

                var temporaryPath = $"{ipcPath}.{Guid.NewGuid():N}.tmp";
                try
                {
                    await File.WriteAllBytesAsync(temporaryPath, payload, ct).ConfigureAwait(false);
                    File.Move(temporaryPath, ipcPath, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                               System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                logger?.LogWarning(exception, "Interactive session probe could not publish a snapshot; it will retry.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }

        logger?.LogInformation("Interactive session probe stopped.");
    }

    private UserSessionSnapshot ProbeInteractiveWindowsSession(DateTimeOffset nowUtc)
    {
        var state = IsDesktopLocked() ? SessionState.Locked : SessionState.Active;
        int? idleSeconds = null;
        var inputInfo = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (GetLastInputInfo(ref inputInfo))
        {
            idleSeconds = checked((int)(unchecked((uint)Environment.TickCount - inputInfo.Time) / 1000));
        }

        var (processName, appName) = GetForegroundProcess();
        return new UserSessionSnapshot(state, idleSeconds, processName, appName, nowUtc);
    }

    private UserSessionSnapshot ProbeWindowsServiceSession(DateTimeOffset nowUtc)
    {
        var sessionId = WtsGetActiveConsoleSessionId();
        if (sessionId == uint.MaxValue)
        {
            return new UserSessionSnapshot(SessionState.NoUser, null, null, null, nowUtc);
        }

        var sessionInfo = QueryWindowsSession(sessionId);
        var hasUserProcess = false;
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                using (process)
                {
                    if (process.SessionId == sessionId)
                    {
                        hasUserProcess = true;
                        break;
                    }
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or
                                               System.ComponentModel.Win32Exception or NotSupportedException)
            {
                process.Dispose();
            }
        }

        if (string.IsNullOrWhiteSpace(sessionInfo.UserName) && !hasUserProcess)
        {
            return new UserSessionSnapshot(SessionState.NoUser, null, null, null, nowUtc);
        }

        var state = sessionInfo.IsLocked ? SessionState.Locked : SessionState.Active;
        var (processName, appName) = FindSessionProcess(sessionId);
        return new UserSessionSnapshot(state, null, processName, appName, nowUtc);
    }

    private static (string? UserName, bool IsLocked) QueryWindowsSession(uint sessionId)
    {
        if (!WtsQuerySessionInformation(
                IntPtr.Zero,
                sessionId,
                WtsInfoExClass,
                out var buffer,
                out _))
        {
            return (null, false);
        }

        try
        {
            var info = Marshal.PtrToStructure<WtsInfoEx>(buffer);
            if (info.Level != 1 || info.Data == IntPtr.Zero)
            {
                return (null, false);
            }

            var levelOne = Marshal.PtrToStructure<WtsInfoExLevel1>(info.Data);
            return (levelOne.UserName, levelOne.SessionFlags == WtsSessionStateLock);
        }
        finally
        {
            WtsFreeMemory(buffer);
        }
    }

    private UserSessionSnapshot ProbeLinuxDesktopSession(DateTimeOffset nowUtc)
    {
        if (!IsLinuxDesktopAvailable())
        {
            return new UserSessionSnapshot(SessionState.NoUser, null, null, null, nowUtc);
        }

        if (IsLinuxDesktopLocked())
        {
            return new UserSessionSnapshot(SessionState.Locked, 0, null, null, nowUtc);
        }

        var foreground = FindLinuxForegroundProcess();
        return new UserSessionSnapshot(
            SessionState.Active,
            foreground.ProcessName is null ? 25 : 5,
            foreground.ProcessName,
            foreground.AppName,
            nowUtc);
    }

    private static bool IsLinuxDesktopAvailable()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")) ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")) ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE")))
        {
            return true;
        }

        try
        {
            return Environment.UserInteractive && GetEffectiveUserId() != 0;
        }
        catch (EntryPointNotFoundException)
        {
            return Environment.UserInteractive && !string.Equals(Environment.UserName, "root", StringComparison.Ordinal);
        }
    }

    private static bool IsLinuxDesktopLocked()
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "gdbus",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            foreach (var argument in new[]
                     {
                         "call", "--session", "--dest", "org.gnome.ScreenSaver",
                         "--object-path", "/org/gnome/ScreenSaver",
                         "--method", "org.gnome.ScreenSaver.GetActive"
                     })
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            if (!process.Start())
            {
                return false;
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(1500))
            {
                process.Kill(entireProcessTree: true);
                return false;
            }

            var output = outputTask.GetAwaiter().GetResult();
            return process.ExitCode == 0 && output.Contains("true", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or
                                           InvalidOperationException or IOException)
        {
            return false;
        }
    }

    private static (string? ProcessName, string? AppName) FindLinuxForegroundProcess()
    {
        var activeProcess = TryGetX11ForegroundProcess();
        if (activeProcess is not null)
        {
            return NormalizeLinuxProcess(activeProcess);
        }

        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                using (process)
                {
                    candidates.Add(process.ProcessName);
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or
                                               System.ComponentModel.Win32Exception or NotSupportedException)
            {
                process.Dispose();
            }
        }

        foreach (var application in LinuxApplications)
        {
            if (application.Names.Any(candidates.Contains))
            {
                return (application.ProcessName, application.DisplayName);
            }
        }

        return (null, null);
    }

    private static string? TryGetX11ForegroundProcess()
    {
        try
        {
            var windowId = RunCommand("xdotool", ["getactivewindow"], TimeSpan.FromSeconds(1));
            if (!ulong.TryParse(windowId?.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var activeWindow))
            {
                return null;
            }

            var pidText = RunCommand(
                "xdotool",
                ["getwindowpid", activeWindow.ToString(CultureInfo.InvariantCulture)],
                TimeSpan.FromSeconds(1));
            if (!int.TryParse(pidText?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var processId))
            {
                return null;
            }

            using var foreground = Process.GetProcessById(processId);
            return foreground.ProcessName;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or
                                           InvalidOperationException or IOException or ArgumentException or
                                           NotSupportedException)
        {
            return null;
        }
    }

    private static string? RunCommand(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start())
        {
            return null;
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            return null;
        }

        return process.ExitCode == 0 ? outputTask.GetAwaiter().GetResult() : null;
    }

    private static (string? ProcessName, string? AppName) NormalizeLinuxProcess(string processName)
    {
        foreach (var application in LinuxApplications)
        {
            if (application.Names.Contains(processName, StringComparer.OrdinalIgnoreCase))
            {
                return (application.ProcessName, application.DisplayName);
            }
        }

        var safeName = TelemetryValidator.SanitizeProcessName(processName);
        return safeName == "Unknown" ? (null, null) : ($"{safeName}.exe", safeName);
    }

    private static bool IsDesktopLocked()
    {
        var desktop = OpenInputDesktop(0, false, 0x0100);
        if (desktop == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var name = new StringBuilder(256);
            return GetUserObjectInformation(desktop, 2, name, (uint)(name.Capacity * sizeof(char)), out _) &&
                string.Equals(name.ToString(), "Winlogon", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CloseDesktop(desktop);
        }
    }

    private static (string? ProcessName, string? AppName) GetForegroundProcess()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero)
        {
            return (null, null);
        }

        GetWindowThreadProcessId(window, out var processId);
        if (processId == 0)
        {
            return (null, null);
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            var processName = process.ProcessName + ".exe";
            var appName = process.MainModule?.FileVersionInfo.ProductName;
            return (processName, string.IsNullOrWhiteSpace(appName) ? null : appName);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                           System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return (null, null);
        }
    }

    private static (string? ProcessName, string? AppName) FindSessionProcess(uint sessionId)
    {
        foreach (var application in LinuxApplications)
        {
            foreach (var process in Process.GetProcessesByName(application.Names[0]))
            {
                try
                {
                    using (process)
                    {
                        if (process.SessionId == sessionId)
                        {
                            return (application.ProcessName, application.DisplayName);
                        }
                    }
                }
                catch (Exception exception) when (exception is InvalidOperationException or
                                                   System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    process.Dispose();
                }
            }
        }

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                using (process)
                {
                    if (process.SessionId == sessionId)
                    {
                        var name = TelemetryValidator.SanitizeProcessName(process.ProcessName);
                        if (name != "Unknown")
                        {
                            return ($"{name}.exe", name);
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or
                                               System.ComponentModel.Win32Exception or NotSupportedException)
            {
                process.Dispose();
            }
        }

        return (null, null);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WtsInfoEx
    {
        public uint Level;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WtsInfoExLevel1
    {
        public uint SessionId;
        public int SessionState;
        public int SessionFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 33)]
        public string WinStationName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 21)]
        public string UserName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 18)]
        public string DomainName;
        public long LogonTime;
        public long ConnectTime;
        public long DisconnectTime;
        public long LastInputTime;
        public long CurrentTime;
    }

    private const int WtsInfoExClass = 25;
    private const int WtsSessionStateLock = 0;

    [DllImport("kernel32.dll", EntryPoint = "WTSGetActiveConsoleSessionId")]
    private static extern uint WtsGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WtsQuerySessionInformation(
        IntPtr server,
        uint sessionId,
        int informationClass,
        out IntPtr buffer,
        out uint bytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WtsFreeMemory(IntPtr memory);

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint desiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(
        IntPtr handle,
        int index,
        StringBuilder information,
        uint length,
        out uint needed);
}
