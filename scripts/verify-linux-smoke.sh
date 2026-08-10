#!/usr/bin/env bash
set -euo pipefail

dotnet_bin="${DOTNET_BIN:-dotnet}"
host_dll="src/AgentForge.Host/bin/Release/net10.0/AgentForge.Host.dll"
log_file="$(mktemp)"

"${dotnet_bin}" "${host_dll}" >"${log_file}" 2>&1 &
host_pid=$!

cleanup() {
  kill "${host_pid}" 2>/dev/null || true
  wait "${host_pid}" 2>/dev/null || true
  rm -f "${log_file}"
}
trap cleanup EXIT

live_status="000"
for _ in {1..120}; do
  live_status="$(curl --silent --output /dev/null --write-out '%{http_code}' http://127.0.0.1:5047/health/live || true)"
  if [[ "${live_status}" == "200" ]]; then
    break
  fi
  sleep 0.25
done

if [[ "${live_status}" != "200" ]]; then
  cat "${log_file}"
  echo "Linux host did not become live (status=${live_status})." >&2
  exit 1
fi

setup_status="$(curl --silent --output /dev/null --write-out '%{http_code}' http://127.0.0.1:5047/api/v1/setup/status)"
runtime_status="$(curl --silent --output /dev/null --write-out '%{http_code}' http://127.0.0.1:5047/api/v1/runtime/ping)"

echo "live=${live_status} setup=${setup_status} runtime=${runtime_status}"
[[ "${setup_status}" == "200" && "${runtime_status}" == "503" ]]
