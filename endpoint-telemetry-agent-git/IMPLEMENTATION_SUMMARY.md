# Endpoint Telemetry Agent — செயல்படுத்திய பணிகள்

இந்த ஆவணம் Endpoint Telemetry Agent workspace-இல் Phase 1 முதல் Phase 4 வரை உருவாக்கப்பட்ட அம்சங்களையும், அவற்றை build/test செய்வதையும் சுருக்கமாக விளக்குகிறது.

## Project அமைப்பு

- `src/Telemetry.Shared` — Agent மற்றும் Server இரண்டும் பயன்படுத்தும் contracts, telemetry models, validation.
- `src/Telemetry.Agent` — Windows/Linux metrics எடுக்கும் background agent, encrypted offline queue, sync.
- `src/Telemetry.Server` — ASP.NET Core API, SQLite storage, dashboard query services, static dashboard.
- `tests/Telemetry.Tests` — Shared contracts, agent storage/sync, session IPC, server/dashboard ஆகியவற்றுக்கான xUnit tests.

## Phase 1 — Shared contracts மற்றும் validation

- Agent, Server இடையே பரிமாறப்படும் `TelemetrySample`, session state, enrollment/batch API contracts உருவாக்கப்பட்டன.
- Dashboard-க்கு தேவையான DTOs சேர்க்கப்பட்டன.
- ஒரே JSON options மூலம் camelCase JSON மற்றும் string enums பயன்படுத்தப்படுகின்றன.
- Sample validation-இல் CPU/memory, sequence number, timestamp, session/idle values, foreground process privacy ஆகியவை சரிபார்க்கப்படுகின்றன.
- Foreground process பெயரில் file path, URL, அல்லது window title இருந்தால் நிராகரிக்கப்படும்.

## Phase 2 — Agent storage மற்றும் data protection

- SQLite அடிப்படையிலான offline sample queue உருவாக்கப்பட்டது.
- Queue-இல் sample சேர்க்கும் முன் payload encrypt செய்யப்படுகிறது; credentials-இல் API token encrypted வடிவில் சேமிக்கப்படுகிறது.
- Monotonic sequence number மற்றும் agent restart count நிலையாகச் சேமிக்கப்படுகின்றன.
- Offline storage அளவு cap-ஐ அடைந்தால் பழைய samples நீக்கப்பட்டு dropped count கணக்கிடப்படுகிறது.
- Server confirmation வந்த பிறகே உறுதிசெய்யப்பட்ட samples உள்ளூர் queue-இலிருந்து நீக்கப்படும்.

தொடர்புடைய கோப்புகள்:

- [IOfflineSampleStore.cs](src/Telemetry.Agent/Storage/IOfflineSampleStore.cs)
- [SqliteOfflineSampleStore.cs](src/Telemetry.Agent/Storage/SqliteOfflineSampleStore.cs)
- [AesGcmDataProtectionService.cs](src/Telemetry.Agent/Security/AesGcmDataProtectionService.cs)

## Phase 3 — Agent worker, collectors, IPC, sync

- Configurable retry delay, maximum delay, retry count, HTTP timeout, device name override ஆகியவை `AgentOptions`-இல் சேர்க்கப்பட்டன.
- Machine CPU/RAM metrics Linux-இல் `/proc` வழியாகவும், Windows-இல் native APIs வழியாகவும் எடுக்கப்படும்.
- User session monitor IPC JSON-ஐ size, timestamp, enum, idle value, process privacy விதிகளுடன் validate செய்கிறது.
- Optional `session-ipc.json` கோப்பைப் பயன்படுத்தி session snapshot வழங்கலாம்.
- Malformed IPC அல்லது probe failure வந்தாலும் monitor தொடர்ந்து இயங்கும்.
- Sync client enrollment மற்றும் batch upload செய்கிறது; transient failure-இல் exponential backoff பயன்படுத்துகிறது.
- Retry-களில் ஒரே sample IDs மற்றும் sequence numbers கொண்ட batch மீண்டும் அனுப்பப்படுகிறது.
- Background worker sample-ஐ முதலில் local storage-இல் சேமித்த பிறகே sync செய்கிறது.
- Windows Service registration மற்றும் structured logs சேர்க்கப்பட்டன. Logs-இல் API token எழுதப்படாது.

தொடர்புடைய கோப்புகள்:

- [Worker.cs](src/Telemetry.Agent/Worker.cs)
- [MachineMetricsProvider.cs](src/Telemetry.Agent/Collectors/MachineMetricsProvider.cs)
- [UserSessionMonitor.cs](src/Telemetry.Agent/Collectors/UserSessionMonitor.cs)
- [TelemetrySyncOrchestrator.cs](src/Telemetry.Agent/Sync/TelemetrySyncOrchestrator.cs)
- [HttpTelemetrySyncClient.cs](src/Telemetry.Agent/Sync/HttpTelemetrySyncClient.cs)

## Phase 4 — Server API, persistent ingest, Dashboard

### Server storage மற்றும் ingest

- SQLite-இல் `devices`, `samples`, `rejected_requests` tables உருவாக்கப்படுகின்றன; WAL, `synchronous=FULL`, busy timeout பயன்படுத்தப்படுகின்றன.
- Enrollment-இல் cryptographic random API token வழங்கப்பட்டு, DB-இல் token-ன் SHA-256 hash மட்டுமே சேமிக்கப்படுகிறது.
- Batch upload-இல் credentials, payload size, sample count, sample data ஆகியவை validate செய்யப்படுகின்றன.
- Invalid request-கள் status/reason உடன் rejected-request audit table-இல் சேமிக்கப்படுகின்றன.
- Batch ingest transaction-இல் deduplication (sample ID அல்லது device/sequence), duplicate accounting, restart detection, gap tracking-க்கு தேவையான samples ஆகியவை சேமிக்கப்படுகின்றன.

### HTTP endpoints

| Method | Route | பயன்பாடு |
|---|---|---|
| `POST` | `/api/v1/devices/enroll` | Device enrollment மற்றும் API token வழங்குதல் |
| `POST` | `/api/v1/telemetry/batch` | Authenticated telemetry batch upload |
| `GET` | `/api/v1/dashboard/overview` | Dashboard data; `deviceId` மற்றும் `range=1h/8h/24h` filters |
| `GET` | `/api/v1/health` | Server health status |

API-க்கு Swagger/OpenAPI, Problem Details error handling, JSON string enums ஆகியவை அமைக்கப்பட்டுள்ளன. `wwwroot` static files-இல் உள்ள dashboard-ஐ `GET /` வழங்குகிறது.

### Dashboard

[index.html](src/Telemetry.Server/wwwroot/index.html)-இல் build step தேவையில்லாத vanilla HTML/CSS/JavaScript dashboard உள்ளது. இது ஐந்து விநாடிக்கு ஒருமுறை data refresh செய்து UTC நேரங்களைக் காட்டும். இதில்:

1. Devices மற்றும் Online/Stale/Offline status
2. Latest sample metrics
3. CPU மற்றும் memory history charts; 1h/8h/24h range தேர்வு
4. Session timeline
5. Active/idle/locked time மற்றும் top app usage
6. Samples table
7. Delivery counts மற்றும் sequence gaps
8. Rejected requests
9. Agent health மற்றும் last successful sync

Device தேர்வு செய்தால் அந்த device-க்கான dashboard data காட்டப்படும்; “All devices” மூலம் fleet view-க்கு திரும்பலாம்.

## Build மற்றும் tests

Repository root-இல் இருந்து:

```bash
dotnet build EndpointTelemetry.sln -nologo -p:CheckEolTargetFramework=false
dotnet test EndpointTelemetry.sln --no-build -nologo
```

Phase 4 செயல்படுத்தியபோது சரிபார்க்கப்பட்ட முடிவுகள்:

- Build: **0 warnings, 0 errors**
- Tests: **22 passed, 0 failed**
- HTTP smoke check: dashboard root, health endpoint, Swagger document ஆகியவை பதிலளித்தன.

Tests-இல் server ingest idempotency, sequence gap/backfill, rejected requests, app usage accounting, agent sync retries/confirmation, malformed IPC ஆகியவை அடங்கும்.

## Server-ஐ உள்ளூரில் இயக்குதல்

```bash
dotnet run --project src/Telemetry.Server/Telemetry.Server.csproj -- --urls http://localhost:5152
```

பிறகு:

- Dashboard: `http://localhost:5152/`
- Health: `http://localhost:5152/api/v1/health`
- Swagger UI (Development environment): `http://localhost:5152/swagger`

Server database இயல்பாக `data/server-telemetry.db`-இல் உருவாகும். `ServerOptions` values-ஐ configuration/environment மூலம் மாற்றலாம்.

Agent-இன் `ServerBaseUrl` இயல்பாக `http://localhost:5080`; `AgentOptions` configuration-இல் server URL மற்றும் sampling/sync settings-ஐ அமைக்கவும்.

## சரிபார்ப்பு வரம்பு

Build மற்றும் tests Linux சூழலில் ஓட்டப்பட்டன. Windows-native CPU/RAM/session APIs compile செய்யப்பட்டுள்ளன; ஆனால் அவை Windows 10/11 இயந்திரத்தில் runtime-இல் சோதிக்கப்படவில்லை.
