param(
    [string]$ServerUrl = "http://localhost:5080",
    [string]$DataDirectory = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$serverDll = Join-Path $repoRoot "src/Telemetry.Server/bin/Debug/net8.0/Telemetry.Server.dll"
$agentDll = Join-Path $repoRoot "src/Telemetry.Agent/bin/Debug/net8.0/Telemetry.Agent.dll"
if ([string]::IsNullOrWhiteSpace($DataDirectory)) {
    $DataDirectory = Join-Path $repoRoot "data/demo"
}
$serverData = Join-Path $DataDirectory "server"
$agentData = Join-Path $DataDirectory "agent"
$logs = Join-Path $repoRoot "logs"
New-Item -ItemType Directory -Force -Path $serverData, $agentData, $logs | Out-Null

$serverArguments = "`"$serverDll`" --urls $ServerUrl --ServerOptions:DataDirectory=`"$serverData`""
$server = Start-Process -FilePath "dotnet" -ArgumentList $serverArguments `
    -WorkingDirectory $repoRoot -PassThru `
    -RedirectStandardOutput (Join-Path $logs "demo-server.log") `
    -RedirectStandardError (Join-Path $logs "demo-server-error.log")

try {
    $ready = $false
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        try {
            Invoke-RestMethod "$ServerUrl/api/v1/health" | Out-Null
            $ready = $true
            break
        } catch {
            Start-Sleep -Milliseconds 500
        }
    }
    if (-not $ready) {
        throw "Server failed to start. See logs/demo-server.log."
    }

    $agentArguments = "`"$agentDll`" --AgentOptions:SampleIntervalSeconds=5 --AgentOptions:ServerBaseUrl=$ServerUrl --AgentOptions:DataDirectory=`"$agentData`""
    $agent = Start-Process -FilePath "dotnet" -ArgumentList $agentArguments `
        -WorkingDirectory $repoRoot -PassThru `
        -RedirectStandardOutput (Join-Path $logs "demo-agent.log") `
        -RedirectStandardError (Join-Path $logs "demo-agent-error.log")

    Write-Host "Dashboard: $ServerUrl/"
    Write-Host "Server PID: $($server.Id); Agent PID: $($agent.Id)"
    Write-Host "Stop this script with Ctrl+C to stop the demo processes."
    while (-not $server.HasExited -and -not $agent.HasExited) {
        Start-Sleep -Seconds 2
        $server.Refresh()
        $agent.Refresh()
    }
} finally {
    if ($agent -and -not $agent.HasExited) {
        Stop-Process -Id $agent.Id
        $agent.WaitForExit()
    }
    if (-not $server.HasExited) {
        Stop-Process -Id $server.Id
        $server.WaitForExit()
    }
}
