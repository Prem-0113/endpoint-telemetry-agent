# Run on Windows

1. **Install prerequisites.** On Windows 10 22H2 or Windows 11 x64, install Git for Windows and the .NET 8 SDK (the SDK, not only the runtime). In PowerShell, run:
   ```powershell
   git --version
   dotnet --list-sdks
   ```
   **Expected:** Git prints a version and the SDK list includes .NET 8 (or a later stable SDK supported by the repository's `global.json`).

2. **Clone the repository.** In PowerShell, run:
   ```powershell
   Set-Location "$HOME\Downloads"
   git clone https://github.com/surya062004/endpoint-telemetry-agent.git
   ```
   **Expected:** Git reports that it cloned the repository into `endpoint-telemetry-agent`.

3. **Open the project folder.** In the same PowerShell window, run:
   ```powershell
   Set-Location "$HOME\Downloads\endpoint-telemetry-agent"
   Get-Location
   ```
   **Expected:** The current path ends in `Downloads\endpoint-telemetry-agent`. Use this folder for the following commands.

4. **Restore NuGet packages.** In the project-root PowerShell window, run:
   ```powershell
   dotnet restore EndpointTelemetry.sln
   ```
   **Expected:** Restore completes successfully for the solution projects.

5. **Build the solution.** In the same window, run:
   ```powershell
   dotnet build EndpointTelemetry.sln -nologo -p:CheckEolTargetFramework=false
   ```
   **Expected:** `Build succeeded.`

6. **Start the backend server.** In the project-root window, run:
   ```powershell
   dotnet run --project src/Telemetry.Server/Telemetry.Server.csproj -- --urls http://localhost:5080
   ```
   Leave this as **Terminal 1** running. **Expected:** `Now listening on: http://localhost:5080`.

7. **Open the dashboard.** In a second PowerShell window, run:
   ```powershell
   Start-Process "http://localhost:5080/"
   ```
   **Browser URL:** http://localhost:5080/ . Before the agent starts, an empty dashboard is expected.

8. **Start the Windows telemetry agent.** In the second PowerShell window, first go to the project root, then run the agent command:
   ```powershell
   Set-Location "$HOME\Downloads\endpoint-telemetry-agent"
   dotnet run --project src/Telemetry.Agent/Telemetry.Agent.csproj -- --AgentOptions:SampleIntervalSeconds=5 --AgentOptions:ServerBaseUrl=http://localhost:5080
   ```
   Leave this as **Terminal 2** running in the logged-in desktop session. **Expected:** agent logs show samples captured, batches sent, and batch confirmations.

9. **Verify real telemetry.** Keep Terminals 1 and 2 running. In the dashboard, wait for the device to show **Online** and for sample values to appear; it refreshes automatically. In a third PowerShell window, run:
   ```powershell
   Invoke-RestMethod "http://localhost:5080/api/v1/dashboard/overview?range=1h" | Select-Object -ExpandProperty recentSamples | Select-Object -First 1 | Format-List deviceId,capturedAtUtc,cpuPercent,memoryUsedMb,sessionState,foregroundProcess
   ```
   **Expected:** A recent sample displays the device ID, UTC capture time, measured CPU percentage, physical memory used in MB, session state, and foreground process. Values depend on the Windows machine and current activity; the dashboard should show the same latest sample.
