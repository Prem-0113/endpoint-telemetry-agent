#!/usr/bin/env bash
set -euo pipefail

seconds=1800
while (($#)); do
  case "$1" in
    --seconds)
      seconds="${2:?--seconds requires an integer}"
      shift 2
      ;;
    *)
      echo "Usage: $0 [--seconds 30]" >&2
      exit 2
      ;;
  esac
done
if ! [[ "$seconds" =~ ^[0-9]+$ ]] || ((seconds < 1)); then
  echo "--seconds must be a positive integer." >&2
  exit 2
fi

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
publish_dir="$repo_root/artifacts/publish/linux-x64"
data_root="$(mktemp -d "${TMPDIR:-/tmp}/telemetry-footprint.XXXXXX")"
server_pid=""
agent_pid=""
server_url="http://127.0.0.1:5184"

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
  rm -rf "$data_root"
}
trap cleanup EXIT INT TERM

dotnet publish "$repo_root/src/Telemetry.Agent/Telemetry.Agent.csproj" \
  --configuration Release --runtime linux-x64 --self-contained false --output "$publish_dir"

dotnet "$repo_root/src/Telemetry.Server/bin/Debug/net8.0/Telemetry.Server.dll" \
  --urls "$server_url" "--ServerOptions:DataDirectory=$data_root/server" \
  >"$data_root/server.log" 2>&1 &
server_pid=$!
for _ in $(seq 1 40); do
  if curl --fail --silent "$server_url/api/v1/health" >/dev/null; then break; fi
  sleep 0.5
done
curl --fail --silent --show-error "$server_url/api/v1/health" >/dev/null || {
  echo "Local measurement server failed to start." >&2
  exit 1
}

dotnet "$publish_dir/Telemetry.Agent.dll" \
  "--AgentOptions:SampleIntervalSeconds=5" \
  "--AgentOptions:ServerBaseUrl=$server_url" \
  "--AgentOptions:DataDirectory=$data_root/agent" \
  >"$data_root/agent.log" 2>&1 &
agent_pid=$!

echo "Measuring published agent for $seconds seconds..."
python3 - "$agent_pid" "$seconds" "$publish_dir" <<'PY'
import os
import pathlib
import sys
import time

pid = int(sys.argv[1])
duration = int(sys.argv[2])
publish_dir = pathlib.Path(sys.argv[3])
clock_ticks = os.sysconf(os.sysconf_names["SC_CLK_TCK"])
page_size = os.sysconf("SC_PAGE_SIZE")
started = time.monotonic()
previous_time = started
previous_cpu = None
cpu_percentages = []
peak_rss_mb = 0.0
deadline = started + duration
while time.monotonic() < deadline:
    try:
        contents = pathlib.Path(f"/proc/{pid}/stat").read_text()
        fields = contents[contents.rfind(")") + 2:].split()
        cpu_ticks = int(fields[11]) + int(fields[12])
        rss_mb = int(fields[21]) * page_size / (1024 * 1024)
        current_time = time.monotonic()
        peak_rss_mb = max(peak_rss_mb, rss_mb)
        if previous_cpu is not None and current_time > previous_time:
            cpu_seconds = (cpu_ticks - previous_cpu) / clock_ticks
            cpu_percentages.append(cpu_seconds / (current_time - previous_time) / (os.cpu_count() or 1) * 100)
        previous_cpu, previous_time = cpu_ticks, current_time
    except (FileNotFoundError, ProcessLookupError):
        raise SystemExit("Agent exited before the measurement finished.")
    time.sleep(min(1, max(0, deadline - time.monotonic())))

install_size_mb = sum(path.stat().st_size for path in publish_dir.rglob("*") if path.is_file()) / (1024 * 1024)
average_cpu = sum(cpu_percentages) / len(cpu_percentages) if cpu_percentages else 0
print(f"Published install size: {install_size_mb:.2f} MB")
print(f"Average agent CPU:      {average_cpu:.2f}% (normalized by logical CPU count)")
print(f"Peak agent RSS:         {peak_rss_mb:.2f} MB")
print("Measurement basis: 1-second /proc process-stat samples while syncing to a local test server.")
PY
