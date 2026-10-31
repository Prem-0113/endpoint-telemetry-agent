param(
    [string]$InstallDirectory = "$env:ProgramFiles\EndpointTelemetryAgent",
    [string]$DataDirectory = "$env:ProgramData\EndpointTelemetryAgent\data",
    [string]$ServerUrl = "http://localhost:5080"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$serviceName = "EndpointTelemetryAgent"
$taskName = "EndpointTelemetryAgent-SessionProbe"
$currentIdentity = "$env:USERDOMAIN\$env:USERNAME"

$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this installer from an elevated PowerShell session."
}

$publishDirectory = Join-Path $repoRoot "artifacts/publish/win-x64"
dotnet publish (Join-Path $repoRoot "src/Telemetry.Agent/Telemetry.Agent.csproj") `
    --configuration Release --runtime win-x64 --self-contained false `
    --output $publishDirectory
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

New-Item -ItemType Directory -Force -Path $InstallDirectory, $DataDirectory | Out-Null
Copy-Item -Path (Join-Path $publishDirectory "*") -Destination $InstallDirectory -Recurse -Force
$agentExe = Join-Path $InstallDirectory "Telemetry.Agent.exe"
if (-not (Test-Path $agentExe)) { throw "Published agent executable not found: $agentExe" }

icacls.exe $DataDirectory /grant "SYSTEM:(OI)(CI)F" "$($currentIdentity):(OI)(CI)M" /T | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Could not set service/probe data directory permissions." }

$serviceArguments = "`"$agentExe`" --AgentOptions:DataDirectory=`"$DataDirectory`" --AgentOptions:ServerBaseUrl=$ServerUrl"
& sc.exe stop $serviceName 2>$null | Out-Null
& sc.exe delete $serviceName 2>$null | Out-Null
Start-Sleep -Seconds 1
& sc.exe create $serviceName "binPath= $serviceArguments" "start= auto" "obj= LocalSystem" "DisplayName= Endpoint Telemetry Agent"
if ($LASTEXITCODE -ne 0) { throw "sc.exe create failed with exit code $LASTEXITCODE." }
& sc.exe description $serviceName "Collects endpoint telemetry and syncs it to the configured server."
if ($LASTEXITCODE -ne 0) { throw "Could not set service description." }
& sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/10000/restart/30000
if ($LASTEXITCODE -ne 0) { throw "Could not configure service recovery actions." }

$probeArguments = "--session-probe --AgentOptions:DataDirectory=`"$DataDirectory`""
$probeAction = New-ScheduledTaskAction -Execute $agentExe -Argument $probeArguments -WorkingDirectory $InstallDirectory
$probeTrigger = New-ScheduledTaskTrigger -AtLogOn -User $currentIdentity
$probePrincipal = New-ScheduledTaskPrincipal -UserId $currentIdentity -LogonType Interactive -RunLevel Limited
$probeSettings = New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit ([TimeSpan]::Zero)
Register-ScheduledTask -TaskName $taskName -Action $probeAction -Trigger $probeTrigger `
    -Principal $probePrincipal -Settings $probeSettings -Description "Publishes privacy-validated interactive session snapshots for the Session 0 agent." `
    -Force | Out-Null
Start-ScheduledTask -TaskName $taskName
Start-Service -Name $serviceName

Write-Host "Installed $serviceName as LocalSystem and registered the per-user session probe."
Write-Host "Shared session IPC and agent data directory: $DataDirectory"
Write-Host "Server URL: $ServerUrl"
