#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
server_dll="$repo_root/src/Telemetry.Server/bin/Debug/net8.0/Telemetry.Server.dll"
agent_dll="$repo_root/src/Telemetry.Agent/bin/Debug/net8.0/Telemetry.Agent.dll"
data_root="${TELEMETRY_SCENARIO_DATA:-$(mktemp -d "${TMPDIR:-/tmp}/telemetry-scenarios.XXXXXX")}"
base_url="${TELEMETRY_SERVER_URL:-http://localhost:5080}"
server_data="$data_root/server"
agent_data="$data_root/agent"
logs="$data_root/logs"
server_pid=""
agent_pid=""
created_data=0

if [[ -z "${TELEMETRY_SCENARIO_DATA:-}" ]]; then created_data=1; fi
mkdir -p "$server_data" "$agent_data" "$logs"

stop_process() {
  local pid="$1"
  if [[ -n "$pid" ]] && kill -0 "$pid" 2>/dev/null; then
    kill "$pid"
    wait "$pid" 2>/dev/null || true
  fi
}

cleanup() {
  stop_process "$agent_pid"
  stop_process "$server_pid"
  if [[ "$created_data" -eq 1 ]]; then
    rm -rf "$data_root"
  fi
}
trap cleanup EXIT INT TERM

start_server() {
  dotnet "$server_dll" \
    --urls "$base_url" "--ServerOptions:DataDirectory=$server_data" \
    >>"$logs/server.log" 2>&1 &
  server_pid=$!
  for _ in $(seq 1 40); do
    if curl --fail --silent "$base_url/api/v1/health" >/dev/null; then return; fi
    sleep 0.5
  done
  echo "Server failed to start. See $logs/server.log." >&2
  exit 1
}

start_agent() {
  dotnet "$agent_dll" \
    "--AgentOptions:SampleIntervalSeconds=1" \
    "--AgentOptions:MaxRetriesPerCycle=0" \
    "--AgentOptions:ServerBaseUrl=$base_url" \
    "--AgentOptions:DataDirectory=$agent_data" \
    >>"$logs/agent.log" 2>&1 &
  agent_pid=$!
}

stop_agent() {
  stop_process "$agent_pid"
  agent_pid=""
}

json_request() {
  local device_id="$1" token="$2" payload="$3"
  curl --fail --silent --show-error "$base_url/api/v1/telemetry/batch" \
    -H "X-Device-Id: $device_id" \
    -H "Authorization: Bearer $token" \
    -H "Content-Type: application/json" \
    --data-binary "$payload"
}

make_session_snapshot() {
  local state="$1" idle="$2"
  python3 -c 'import datetime,json,sys; state=sys.argv[1]; idle=None if sys.argv[2]=="null" else int(sys.argv[2]); now=datetime.datetime.now(datetime.timezone.utc).isoformat(); print(json.dumps({"sessionState":state,"idleSeconds":idle,"foregroundProcess":"code.exe" if state=="Active" else None,"foregroundAppName":"Visual Studio Code" if state=="Active" else None,"capturedAtUtc":now}))' "$state" "$idle" >"$agent_data/session-ipc.json.tmp"
  mv "$agent_data/session-ipc.json.tmp" "$agent_data/session-ipc.json"
}

start_server
start_agent
echo "Waiting for agent enrollment and first sample..."
for _ in $(seq 1 30); do
  if curl --fail --silent "$base_url/api/v1/dashboard/overview?range=1h" |
      python3 -c 'import json,sys; d=json.load(sys.stdin); sys.exit(0 if d["deviceStatus"] else 1)' 2>/dev/null; then break; fi
  sleep 1
done

echo "Scenario 1/5: queue samples while server is offline, restart and backfill."
sleep 3
kill "$server_pid"
wait "$server_pid" 2>/dev/null || true
server_pid=""
sleep 7
stop_agent
start_server
start_agent
sleep 5

echo "Scenario 2/5: resend the same batch and verify duplicate accounting."
enrollment="$(curl --fail --silent --show-error "$base_url/api/v1/devices/enroll" \
  -H "Content-Type: application/json" \
  --data '{"deviceName":"scenario-duplicate","machineGuid":"scenario-duplicate-'$(date +%s%N)'","osVersion":"Demo","agentVersion":"1.0.0"}')"
device_id="$(python3 -c 'import json,sys; print(json.loads(sys.argv[1])["deviceId"])' "$enrollment")"
token="$(python3 -c 'import json,sys; print(json.loads(sys.argv[1])["apiToken"])' "$enrollment")"
duplicate_payload="$(python3 -c 'import datetime,json,sys,time; now=datetime.datetime.now(datetime.timezone.utc).isoformat(); n=time.time_ns(); s={"sampleId":"22222222-2222-4222-8222-222222222222","capturedAtUtc":now,"cpuPercent":12.5,"memoryUsedMb":4096,"sessionState":"Active","idleSeconds":5,"foregroundProcess":"code.exe","foregroundAppName":"Visual Studio Code","agentVersion":"1.0.0","sequenceNumber":n,"agentStartedAtUtc":now,"queueDepth":1}; print(json.dumps({"deviceId":sys.argv[1],"droppedSamplesCount":0,"samples":[s]}))' "$device_id")"
json_request "$device_id" "$token" "$duplicate_payload" >/dev/null
json_request "$device_id" "$token" "$duplicate_payload" >/dev/null

echo "Scenario 3/5: publish locked snapshots, then active snapshots."
make_session_snapshot "Locked" 0
sleep 3
make_session_snapshot "Active" 5
sleep 4
rm -f "$agent_data/session-ipc.json"

echo "Scenario 4/5: restart the agent process and allow restart telemetry to sync."
sleep 2
stop_agent
sleep 2
start_agent
sleep 4

echo "Scenario 5/5: send malformed, oversized, and unauthenticated requests."
malformed_status="$(curl --silent --output /dev/null --write-out '%{http_code}' \
  "$base_url/api/v1/telemetry/batch" \
  -H "X-Device-Id: $device_id" -H "Authorization: Bearer $token" \
  -H "Content-Type: application/json" --data '{broken')"
oversized_body="$data_root/oversized.json"
python3 -c 'print("x" * (1024 * 1024 + 128), end="")' >"$oversized_body"
oversized_status="$(curl --silent --output /dev/null --write-out '%{http_code}' \
  "$base_url/api/v1/telemetry/batch" -H "Content-Type: application/json" --data-binary "@$oversized_body")"
unauth_status="$(curl --silent --output /dev/null --write-out '%{http_code}' \
  "$base_url/api/v1/telemetry/batch" -H "Content-Type: application/json" \
  --data '{"deviceId":"unknown","droppedSamplesCount":0,"samples":[]}')"

echo "Rejected request statuses: malformed=$malformed_status oversized=$oversized_status unauthenticated=$unauth_status"
echo "Dashboard: $base_url/"
echo "Scenario data is retained at $data_root while this script runs and cleaned up on exit."
echo "Press Ctrl+C to stop the processes and remove temporary scenario data."
while kill -0 "$server_pid" 2>/dev/null && kill -0 "$agent_pid" 2>/dev/null; do sleep 2; done
