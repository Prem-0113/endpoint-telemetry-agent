param(
    [string]$ServerUrl = "http://localhost:5080",
    [string]$DataDirectory = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$serverDll = Join-Path $repoRoot "src/Telemetry.Server/bin/Debug/net8.0/Telemetry.Server.dll"
$agentDll = Join-Path $repoRoot "src/Telemetry.Agent/bin/Debug/net8.0/Telemetry.Agent.dll"
$temporaryData = [string]::IsNullOrWhiteSpace($DataDirectory)
if ($temporaryData) {
    $DataDirectory = Join-Path $env:TEMP ("telemetry-scenarios-" + [guid]::NewGuid().ToString("N"))
}
$serverData = Join-Path $DataDirectory "server"
$agentData = Join-Path $DataDirectory "agent"
$logs = Join-Path $DataDirectory "logs"
New-Item -ItemType Directory -Force -Path $serverData, $agentData, $logs | Out-Null
$server = $null
$agent = $null

function Start-ScenarioServer {
    $arguments = "`"$serverDll`" --urls $ServerUrl --ServerOptions:DataDirectory=`"$serverData`""
    $script:server = Start-Process -FilePath "dotnet" -ArgumentList $arguments -WorkingDirectory $repoRoot -PassThru `
        -RedirectStandardOutput (Join-Path $logs "server.log") -RedirectStandardError (Join-Path $logs "server-error.log")
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        try {
            Invoke-RestMethod "$ServerUrl/api/v1/health" | Out-Null
            return
        } catch {
            Start-Sleep -Milliseconds 500
        }
    }
    throw "Server failed to start. See $logs/server.log."
}

function Start-ScenarioAgent {
    $arguments = "`"$agentDll`" --AgentOptions:SampleIntervalSeconds=1 --AgentOptions:MaxRetriesPerCycle=0 --AgentOptions:ServerBaseUrl=$ServerUrl --AgentOptions:DataDirectory=`"$agentData`""
    $script:agent = Start-Process -FilePath "dotnet" -ArgumentList $arguments -WorkingDirectory $repoRoot -PassThru `
        -RedirectStandardOutput (Join-Path $logs "agent.log") -RedirectStandardError (Join-Path $logs "agent-error.log")
}

function Stop-ScenarioProcess([System.Diagnostics.Process]$Process) {
    if ($null -ne $Process) {
        $Process.Refresh()
        if (-not $Process.HasExited) {
            Stop-Process -Id $Process.Id
            $Process.WaitForExit()
        }
    }
}

function Send-Batch([string]$DeviceId, [string]$Token, $Payload) {
    Invoke-RestMethod "$ServerUrl/api/v1/telemetry/batch" -Method Post `
        -Headers @{ "X-Device-Id" = $DeviceId; Authorization = "Bearer $Token" } `
        -ContentType "application/json" -Body ($Payload | ConvertTo-Json -Depth 8) | Out-Null
}

function New-Snapshot([string]$State, $IdleSeconds) {
    $snapshot = @{
        sessionState = $State
        idleSeconds = $IdleSeconds
        foregroundProcess = if ($State -eq "Active") { "code.exe" } else { $null }
        foregroundAppName = if ($State -eq "Active") { "Visual Studio Code" } else { $null }
        capturedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    }
    $temporaryFile = Join-Path $agentData "session-ipc.json.tmp"
    [System.IO.File]::WriteAllText(
        $temporaryFile,
        ($snapshot | ConvertTo-Json -Compress),
        [System.Text.UTF8Encoding]::new($false))
    Move-Item -Force $temporaryFile (Join-Path $agentData "session-ipc.json")
}

try {
    Start-ScenarioServer
    Start-ScenarioAgent
    Write-Host "Waiting for agent enrollment and first sample..."
    $enrolled = $false
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        try {
            if ((Invoke-RestMethod "$ServerUrl/api/v1/dashboard/overview?range=1h").deviceStatus.Count -gt 0) {
                $enrolled = $true
                break
            }
        } catch { }
        Start-Sleep -Seconds 1
    }
    if (-not $enrolled) { throw "Agent did not enroll; inspect logs under $logs." }

    Write-Host "Scenario 1/5: queue samples while server is offline, restart and backfill."
    Start-Sleep -Seconds 3
    Stop-ScenarioProcess $server
    $server = $null
    Start-Sleep -Seconds 7
    Stop-ScenarioProcess $agent
    $agent = $null
    Start-ScenarioServer
    Start-ScenarioAgent
    Start-Sleep -Seconds 5

    Write-Host "Scenario 2/5: resend the same batch and verify duplicate accounting."
    $machineGuid = "scenario-duplicate-" + [guid]::NewGuid().ToString("N")
    $enrollment = Invoke-RestMethod "$ServerUrl/api/v1/devices/enroll" -Method Post `
        -ContentType "application/json" `
        -Body (@{ deviceName = "scenario-duplicate"; machineGuid = $machineGuid; osVersion = "Demo"; agentVersion = "1.0.0" } | ConvertTo-Json)
    $sample = @{
        sampleId = [guid]::NewGuid().ToString()
        capturedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
        cpuPercent = 12.5
        memoryUsedMb = 4096
        sessionState = "Active"
        idleSeconds = 5
        foregroundProcess = "code.exe"
        foregroundAppName = "Visual Studio Code"
        agentVersion = "1.0.0"
        sequenceNumber = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
        agentStartedAtUtc = [DateTimeOffset]::UtcNow.AddMinutes(-1).ToString("O")
        queueDepth = 1
    }
    $duplicateBatch = @{ deviceId = $enrollment.deviceId; droppedSamplesCount = 0; samples = @($sample) }
    Send-Batch $enrollment.deviceId $enrollment.apiToken $duplicateBatch
    Send-Batch $enrollment.deviceId $enrollment.apiToken $duplicateBatch

    Write-Host "Scenario 3/5: publish locked snapshots, then active snapshots."
    New-Snapshot "Locked" 0
    Start-Sleep -Seconds 3
    New-Snapshot "Active" 5
    Start-Sleep -Seconds 4
    Remove-Item -Force (Join-Path $agentData "session-ipc.json") -ErrorAction SilentlyContinue

    Write-Host "Scenario 4/5: restart the agent process and allow restart telemetry to sync."
    Start-Sleep -Seconds 2
    Stop-ScenarioProcess $agent
    $agent = $null
    Start-Sleep -Seconds 2
    Start-ScenarioAgent
    Start-Sleep -Seconds 4

    Write-Host "Scenario 5/5: send malformed, oversized, and unauthenticated requests."
    $statuses = @{}
    try {
        Invoke-WebRequest "$ServerUrl/api/v1/telemetry/batch" -Method Post `
            -Headers @{ "X-Device-Id" = $enrollment.deviceId; Authorization = "Bearer $($enrollment.apiToken)" } `
            -ContentType "application/json" -Body "{broken" | Out-Null
    } catch { $statuses.malformed = [int]$_.Exception.Response.StatusCode }
    try {
        $oversized = "x" * (1MB + 128)
        Invoke-WebRequest "$ServerUrl/api/v1/telemetry/batch" -Method Post -ContentType "application/json" -Body $oversized | Out-Null
    } catch { $statuses.oversized = [int]$_.Exception.Response.StatusCode }
    try {
        Invoke-WebRequest "$ServerUrl/api/v1/telemetry/batch" -Method Post -ContentType "application/json" `
            -Body '{"deviceId":"unknown","droppedSamplesCount":0,"samples":[]}' | Out-Null
    } catch { $statuses.unauthenticated = [int]$_.Exception.Response.StatusCode }

    Write-Host "Rejected request statuses: $($statuses | ConvertTo-Json -Compress)"
    Write-Host "Dashboard: $ServerUrl/"
    Write-Host "Scenario data: $DataDirectory"
    Write-Host "Press Ctrl+C to stop the processes and remove temporary scenario data."
    while (-not $server.HasExited -and -not $agent.HasExited) {
        Start-Sleep -Seconds 2
        $server.Refresh()
        $agent.Refresh()
    }
} finally {
    Stop-ScenarioProcess $agent
    Stop-ScenarioProcess $server
    if ($temporaryData -and (Test-Path $DataDirectory)) {
        Remove-Item -Recurse -Force $DataDirectory
    }
}
