param(
    [int]$Seconds = 1800
)

$ErrorActionPreference = "Stop"
if ($Seconds -lt 1) { throw "Seconds must be a positive integer." }
$repoRoot = Split-Path -Parent $PSScriptRoot
$publishDirectory = Join-Path $repoRoot "artifacts/publish/win-x64"
$temporaryData = Join-Path $env:TEMP ("telemetry-footprint-" + [guid]::NewGuid().ToString("N"))
$serverUrl = "http://127.0.0.1:5185"
$server = $null
$agent = $null
New-Item -ItemType Directory -Force -Path (Join-Path $temporaryData "server"), (Join-Path $temporaryData "agent") | Out-Null

try {
    dotnet publish (Join-Path $repoRoot "src/Telemetry.Agent/Telemetry.Agent.csproj") `
        --configuration Release --runtime win-x64 --self-contained false --output $publishDirectory
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

    $serverDll = Join-Path $repoRoot "src/Telemetry.Server/bin/Debug/net8.0/Telemetry.Server.dll"
    $serverArguments = "`"$serverDll`" --urls $serverUrl --ServerOptions:DataDirectory=`"$(Join-Path $temporaryData 'server')`""
    $server = Start-Process -FilePath "dotnet" -ArgumentList $serverArguments -WorkingDirectory $repoRoot -PassThru `
        -RedirectStandardOutput (Join-Path $temporaryData "server.log") -RedirectStandardError (Join-Path $temporaryData "server-error.log")
    $ready = $false
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        try {
            Invoke-RestMethod "$serverUrl/api/v1/health" | Out-Null
            $ready = $true
            break
        } catch { Start-Sleep -Milliseconds 500 }
    }
    if (-not $ready) { throw "Local measurement server failed to start." }

    $agentExe = Join-Path $publishDirectory "Telemetry.Agent.exe"
    $agentArguments = "--AgentOptions:SampleIntervalSeconds=5 --AgentOptions:ServerBaseUrl=$serverUrl --AgentOptions:DataDirectory=`"$(Join-Path $temporaryData 'agent')`""
    $agent = Start-Process -FilePath $agentExe -ArgumentList $agentArguments -WorkingDirectory $publishDirectory -PassThru
    Write-Host "Measuring published agent for $Seconds seconds..."

    $start = [DateTimeOffset]::UtcNow
    $previous = $agent.TotalProcessorTime.TotalSeconds
    $previousAt = [DateTimeOffset]::UtcNow
    $cpuPercentages = [System.Collections.Generic.List[double]]::new()
    $peakWorkingSetMb = 0.0
    while (([DateTimeOffset]::UtcNow - $start).TotalSeconds -lt $Seconds) {
        Start-Sleep -Seconds 1
        $agent.Refresh()
        if ($agent.HasExited) { throw "Agent exited before the measurement finished." }
        $now = [DateTimeOffset]::UtcNow
        $cpu = $agent.TotalProcessorTime.TotalSeconds
        $elapsed = ($now - $previousAt).TotalSeconds
        if ($elapsed -gt 0) {
            $cpuPercentages.Add((($cpu - $previous) / $elapsed / [Environment]::ProcessorCount) * 100)
        }
        $previous, $previousAt = $cpu, $now
        $peakWorkingSetMb = [Math]::Max($peakWorkingSetMb, $agent.WorkingSet64 / 1MB)
    }

    $installSizeMb = (Get-ChildItem $publishDirectory -File -Recurse | Measure-Object -Property Length -Sum).Sum / 1MB
    $averageCpu = if ($cpuPercentages.Count) { ($cpuPercentages | Measure-Object -Average).Average } else { 0 }
    Write-Host ("Published install size: {0:N2} MB" -f $installSizeMb)
    Write-Host ("Average agent CPU:      {0:N2}% (normalized by logical CPU count)" -f $averageCpu)
    Write-Host ("Peak agent working set: {0:N2} MB" -f $peakWorkingSetMb)
    Write-Host "Measurement basis: 1-second process CPU and working-set samples while syncing to a local test server."
} finally {
    if ($agent -and -not $agent.HasExited) { Stop-Process -Id $agent.Id; $agent.WaitForExit() }
    if ($server -and -not $server.HasExited) { Stop-Process -Id $server.Id; $server.WaitForExit() }
    if (Test-Path $temporaryData) { Remove-Item -Recurse -Force $temporaryData }
}
