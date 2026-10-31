#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
server_dll="$repo_root/src/Telemetry.Server/bin/Debug/net8.0/Telemetry.Server.dll"
agent_dll="$repo_root/src/Telemetry.Agent/bin/Debug/net8.0/Telemetry.Agent.dll"
data_root="${TELEMETRY_DEMO_DATA:-$repo_root/data/demo}"
base_url="${TELEMETRY_SERVER_URL:-http://localhost:5080}"
server_pid=""
agent_pid=""

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
}
trap cleanup EXIT INT TERM

mkdir -p "$data_root/server" "$data_root/agent" "$repo_root/logs"

dotnet "$server_dll" \
  --urls "$base_url" \
  "--ServerOptions:DataDirectory=$data_root/server" \
  >"$repo_root/logs/demo-server.log" 2>&1 &
server_pid=$!

for _ in $(seq 1 40); do
  if curl --fail --silent "$base_url/api/v1/health" >/dev/null; then
    break
  fi
  sleep 0.5
done
curl --fail --silent --show-error "$base_url/api/v1/health" >/dev/null || {
  echo "Server failed to start. See logs/demo-server.log." >&2
  exit 1
}

dotnet "$agent_dll" \
  "--AgentOptions:SampleIntervalSeconds=5" \
  "--AgentOptions:ServerBaseUrl=$base_url" \
  "--AgentOptions:DataDirectory=$data_root/agent" \
  >"$repo_root/logs/demo-agent.log" 2>&1 &
agent_pid=$!

echo "Dashboard: $base_url/"
echo "Server log: $repo_root/logs/demo-server.log"
echo "Agent log:  $repo_root/logs/demo-agent.log"
echo "Press Ctrl+C to stop the demo processes."
while kill -0 "$server_pid" 2>/dev/null && kill -0 "$agent_pid" 2>/dev/null; do
  sleep 2
done
