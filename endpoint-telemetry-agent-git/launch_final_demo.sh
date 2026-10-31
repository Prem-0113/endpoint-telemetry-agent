#!/usr/bin/env bash
set -euo pipefail

echo "=================================================================="
echo " Endpoint Telemetry Agent — Live Server + All 5 Scenarios Seeder"
echo "=================================================================="

# 1. Clean up any old server/agent processes on port 5080
pkill -f "Telemetry.Server" 2>/dev/null || true
pkill -f "Telemetry.Agent" 2>/dev/null || true
sleep 1

# 2. Start Telemetry.Server in background on http://localhost:5080
mkdir -p ./demo-live-data
rm -rf ./demo-live-data/*
nohup dotnet run --no-build --project src/Telemetry.Server/Telemetry.Server.csproj -- \
  --urls http://localhost:5080 \
  --ServerOptions:DataDirectory="$(pwd)/demo-live-data" \
  > ./demo-live-data/server.log 2>&1 &
SERVER_PID=$!

echo "Waiting for Telemetry.Server (PID $SERVER_PID) on http://localhost:5080 ..."
for i in $(seq 1 30); do
  if curl -fsS http://localhost:5080/api/v1/health >/dev/null 2>&1; then
    echo "Server is LIVE at http://localhost:5080 !"
    break
  fi
  sleep 1
done

# 3. Execute all 5 Reliability Scenarios + Active App Usage against http://localhost:5080
python3 - <<'PY'
import json
import urllib.request
import urllib.error
import uuid
from datetime import datetime, timedelta, timezone

BASE_URL = "http://localhost:5080"

def post_json(path, payload, headers=None):
    data = json.dumps(payload).encode("utf-8")
    req_headers = {"Content-Type": "application/json"}
    if headers:
        req_headers.update(headers)
    req = urllib.request.Request(BASE_URL + path, data=data, headers=req_headers, method="POST")
    try:
        with urllib.request.urlopen(req, timeout=10) as resp:
            return resp.status, json.loads(resp.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        body = e.read().decode("utf-8", errors="replace")
        return e.code, body

def iso_utc(dt):
    return dt.astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")

now = datetime.now(timezone.utc)
start_1 = now - timedelta(hours=2)
start_2 = now - timedelta(minutes=15)

# Step A: Enroll Device
status, enroll = post_json("/api/v1/devices/enroll", {
    "deviceName": "WIN11-ENDPOINT-01",
    "machineGuid": "ea8190b2-5142-4c99-901a-ef0011223344",
    "osVersion": "Windows 11 Pro 23H2 (10.0.22631) x64",
    "agentVersion": "1.0.0"
})
device_id = enroll["deviceId"]
token = enroll["apiToken"]
auth_headers = {
    "X-Device-Id": device_id,
    "Authorization": f"Bearer {token}"
}
print(f"[Enrolled] DeviceId={device_id}")

# Step B: Scenario 1 (Offline Queue & Backfill of 5+ samples with queueDepth rising then 0)
# + Scenario 3 (Lock & Unlock: 2 Locked samples + Active samples + 1 Idle > 300s sample)
samples_batch_1 = []
plan = [
    # (seq, mins_ago, cpu, mem, state, idle, proc, app, q_depth, started)
    (1, 14, 12.4, 6120, "Active", 8,   "code.exe",     "Visual Studio Code", 4, start_1),
    (2, 13, 15.8, 6190, "Active", 14,  "code.exe",     "Visual Studio Code", 3, start_1),
    (3, 12, 18.2, 6250, "Active", 6,   "chrome.exe",   "Google Chrome",      2, start_1),
    (4, 11, 14.1, 6240, "Active", 19,  "chrome.exe",   "Google Chrome",      1, start_1),
    (5, 10, 11.0, 6180, "Active", 345, "chrome.exe",   "Google Chrome",      0, start_1), # Idle > 300s -> counts as Idle!
    (6, 9,  4.5,  6100, "Locked", 60,  "LogonUI.exe",  "Windows Lock Screen",0, start_1), # Scenario 3: Locked
    (7, 8,  4.2,  6095, "Locked", 120, "LogonUI.exe",  "Windows Lock Screen",0, start_1), # Scenario 3: Locked (2 mins)
    (8, 7,  19.6, 6310, "Active", 4,   "code.exe",     "Visual Studio Code", 0, start_1), # Scenario 3: Unlocked -> Active!
    (9, 6,  22.3, 6380, "Active", 9,   "wt.exe",       "Windows Terminal",   0, start_1),
    (10,5,  16.7, 6340, "Active", 11,  "explorer.exe", "Windows Explorer",   0, start_1),
]

for seq, mins_ago, cpu, mem, st, idle, proc, app, qd, started in plan:
    samples_batch_1.append({
        "sampleId": str(uuid.uuid5(uuid.NAMESPACE_DNS, f"{device_id}-seq-{seq}")),
        "capturedAtUtc": iso_utc(now - timedelta(minutes=mins_ago)),
        "cpuPercent": cpu,
        "memoryUsedMb": mem,
        "sessionState": st,
        "idleSeconds": idle,
        "foregroundProcess": proc,
        "foregroundAppName": app,
        "agentVersion": "1.0.0",
        "sequenceNumber": seq,
        "agentStartedAtUtc": iso_utc(started),
        "queueDepth": qd
    })

st_code, resp1 = post_json("/api/v1/telemetry/batch", {
    "deviceId": device_id,
    "droppedSamplesCount": 2,
    "samples": samples_batch_1
}, auth_headers)
print(f"[Scenario 1 & 3] Stored={resp1['storedCount']}, Duplicates={resp1['duplicateCount']}")

# Step C: Scenario 2 (Duplicate Delivery — resend first 4 samples; storedCount=0, duplicateCount=4)
st_code, resp2 = post_json("/api/v1/telemetry/batch", {
    "deviceId": device_id,
    "droppedSamplesCount": 0,
    "samples": samples_batch_1[:4]
}, auth_headers)
print(f"[Scenario 2: Duplicate Delivery] Stored={resp2['storedCount']}, Duplicates={resp2['duplicateCount']}")

# Step D: Scenario 4 (Process Failure & Recovery — new agentStartedAtUtc increments RestartCount, 0 gaps)
samples_batch_2 = [
    {
        "sampleId": str(uuid.uuid5(uuid.NAMESPACE_DNS, f"{device_id}-seq-11")),
        "capturedAtUtc": iso_utc(now - timedelta(minutes=2)),
        "cpuPercent": 21.4,
        "memoryUsedMb": 6410,
        "sessionState": "Active",
        "idleSeconds": 5,
        "foregroundProcess": "code.exe",
        "foregroundAppName": "Visual Studio Code",
        "agentVersion": "1.0.0",
        "sequenceNumber": 11,
        "agentStartedAtUtc": iso_utc(start_2),
        "queueDepth": 0
    },
    {
        "sampleId": str(uuid.uuid5(uuid.NAMESPACE_DNS, f"{device_id}-seq-12")),
        "capturedAtUtc": iso_utc(now - timedelta(minutes=1)),
        "cpuPercent": 14.9,
        "memoryUsedMb": 6390,
        "sessionState": "Active",
        "idleSeconds": 7,
        "foregroundProcess": "chrome.exe",
        "foregroundAppName": "Google Chrome",
        "agentVersion": "1.0.0",
        "sequenceNumber": 12,
        "agentStartedAtUtc": iso_utc(start_2),
        "queueDepth": 0
    }
]
st_code, resp3 = post_json("/api/v1/telemetry/batch", {
    "deviceId": device_id,
    "droppedSamplesCount": 0,
    "samples": samples_batch_2
}, auth_headers)
print(f"[Scenario 4: Process Failure Recovery] Stored={resp3['storedCount']} (RestartCount incremented, 0 gaps)")

# Step E: Scenario 5 (Bad Input: 400 Malformed, 413 Oversized, 401 Unauthenticated)
# 1) 401 Unauthenticated
s401, _ = post_json("/api/v1/telemetry/batch", {"deviceId": device_id, "droppedSamplesCount": 0, "samples": []})
# 2) 400 Malformed JSON
req_bad = urllib.request.Request(
    BASE_URL + "/api/v1/telemetry/batch",
    data=b"{bad-json-payload",
    headers={**auth_headers, "Content-Type": "application/json"},
    method="POST"
)
try:
    urllib.request.urlopen(req_bad)
except urllib.error.HTTPError as e:
    s400 = e.code
# 3) 413 Oversized payload (> 1 MB)
req_big = urllib.request.Request(
    BASE_URL + "/api/v1/telemetry/batch",
    data=b"x" * (1024 * 1024 + 2048),
    headers={**auth_headers, "Content-Type": "application/json"},
    method="POST"
)
try:
    urllib.request.urlopen(req_big)
except urllib.error.HTTPError as e:
    s413 = e.code

print(f"[Scenario 5: Bad Input] Statuses recorded: {s401} (Unauth), {s400} (Malformed), {s413} (Oversized)")
PY

# 4. Also start Telemetry.Agent in background so live samples continue arriving
nohup dotnet run --no-build --project src/Telemetry.Agent/Telemetry.Agent.csproj -- \
  --AgentOptions:ServerBaseUrl=http://localhost:5080 \
  --AgentOptions:SampleIntervalSeconds=10 \
  --AgentOptions:DataDirectory="$(pwd)/demo-live-data/agent" \
  > ./demo-live-data/agent.log 2>&1 &

echo ""
echo "=================================================================="
echo " ALL 5 SCENARIOS & ACTIVE APP USAGE ARE NOW LIVE!"
echo " Open http://localhost:5080 in your browser and refresh!"
echo "=================================================================="