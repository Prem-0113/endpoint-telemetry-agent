param(
    [string]$InstallDirectory = "$env:ProgramFiles\EndpointTelemetryAgent"
)

$ErrorActionPreference = "Stop"
$serviceName = "EndpointTelemetryAgent"
$taskName = "EndpointTelemetryAgent-SessionProbe"

$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this uninstaller from an elevated PowerShell session."
}

if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
    Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
}

if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    & sc.exe delete $serviceName
    if ($LASTEXITCODE -ne 0) { throw "sc.exe delete failed with exit code $LASTEXITCODE." }
}

if (Test-Path $InstallDirectory) {
    Remove-Item -Path $InstallDirectory -Recurse -Force
}

Write-Host "Service, session-probe task, and program files removed."
Write-Host "Agent data under ProgramData is intentionally preserved. Remove it separately if no longer needed."
