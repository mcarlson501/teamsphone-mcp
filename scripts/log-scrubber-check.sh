#!/usr/bin/env bash
# Log-scrubber gate (build spec §7, §10, §15 S1).
#
# Seeds the host with known credential-shaped material, drives a representative
# run over the HTTP transport (unauthenticated reject, initialize, tools/list,
# a tier-0 tool call, and two failing credential resolutions), captures every
# byte the host writes to stdout and stderr, and fails if any seeded secret,
# PEM private key, or PFX/base64 key marker reaches the log.
#
# Usage: ./scripts/log-scrubber-check.sh
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

port="${LOG_SCRUBBER_PORT:-58211}"
workdir="$(mktemp -d)"
log_file="$workdir/host.log"
server_pid=""

cleanup() {
  if [[ -n "$server_pid" ]] && kill -0 "$server_pid" 2>/dev/null; then
    kill "$server_pid" 2>/dev/null || true
    wait "$server_pid" 2>/dev/null || true
  fi
  rm -rf "$workdir"
}
trap cleanup EXIT

# ---------------------------------------------------------------------------
# Seeded secrets. None of these are real; all of them must stay out of the log.
# ---------------------------------------------------------------------------
seeded_thumbprint="A1B2C3D4E5F60718293A4B5C6D7E8F9012345678"
seeded_pfx_password="Pfx-$(openssl rand -hex 16)"
bearer_token="$(openssl rand -hex 32)"
wrong_token="wrong-$(openssl rand -hex 16)"
seeded_key_body="$(openssl rand -base64 512 | tr -d '\n')"

# Emits a syntactically valid but entirely synthetic PEM, regenerated per run.
write_seeded_pem() {
  printf -- '-----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAKCAQEA%s\n-----END RSA PRIVATE KEY-----\n' \
    "$seeded_key_body" >>"$1"
}

cert_file="$workdir/seeded.pfx"
write_seeded_pem "$cert_file"

echo "Building host..."
# Quiet on success, but surface the compiler output on failure: swallowing it
# turns any build break into a bare "exit 1" from this gate.
if ! build_output="$(dotnet build src/TeamsPhoneMcp.Host/TeamsPhoneMcp.Host.csproj \
  --configuration Release 2>&1)"; then
  echo "error: the host failed to build, so the scrubber has nothing to scan." >&2
  echo "$build_output" >&2
  exit 1
fi

echo "Starting host on 127.0.0.1:${port}..."
env \
  ASPNETCORE_URLS="http://127.0.0.1:${port}" \
  ASPNETCORE_ENVIRONMENT=Production \
  DOTNET_ENVIRONMENT=Production \
  TEAMSPHONE_MCP_BEARER_TOKEN="$bearer_token" \
  TEAMSPHONE_MCP_MODE=whatif \
  Audit__RootPath="$workdir/audit" \
  Logging__LogLevel__Default=Debug \
  "Credentials__scrub-thumbprint__TenantId=00000000-0000-0000-0000-0000000000aa" \
  "Credentials__scrub-thumbprint__ClientId=00000000-0000-0000-0000-0000000000bb" \
  "Credentials__scrub-thumbprint__CertificateThumbprint=${seeded_thumbprint}" \
  "Credentials__scrub-path__TenantId=00000000-0000-0000-0000-0000000000aa" \
  "Credentials__scrub-path__ClientId=00000000-0000-0000-0000-0000000000bb" \
  "Credentials__scrub-path__CertificatePath=${cert_file}" \
  "Credentials__scrub-path__CertificatePasswordEnvVar=LOG_SCRUBBER_PFX_PASSWORD" \
  LOG_SCRUBBER_PFX_PASSWORD="$seeded_pfx_password" \
  dotnet run --project src/TeamsPhoneMcp.Host/TeamsPhoneMcp.Host.csproj \
  --configuration Release --no-build --no-launch-profile \
  >"$log_file" 2>&1 &
server_pid=$!

base_url="http://127.0.0.1:${port}/mcp"
ready=false
for _ in $(seq 1 60); do
  if [[ "$(curl --silent --output /dev/null --write-out '%{http_code}' --request POST "$base_url" || true)" == "401" ]]; then
    ready=true
    break
  fi
  sleep 1
done
if [[ "$ready" != true ]]; then
  echo "error: host did not become ready. Captured output:" >&2
  cat "$log_file" >&2
  exit 1
fi

mcp_call() {
  local body="$1"
  shift
  curl --silent --show-error --output /dev/null "$@" \
    --request POST "$base_url" \
    --header "Authorization: Bearer ${bearer_token}" \
    --header 'Content-Type: application/json' \
    --header 'Accept: application/json, text/event-stream' \
    --data "$body" || true
}

echo "Driving a representative run..."

# Unauthenticated and wrong-token rejections.
curl --silent --output /dev/null --request POST "$base_url" || true
curl --silent --output /dev/null --request POST "$base_url" \
  --header "Authorization: Bearer ${wrong_token}" || true

headers_file="$workdir/initialize.headers"
curl --silent --output /dev/null --dump-header "$headers_file" \
  --request POST "$base_url" \
  --header "Authorization: Bearer ${bearer_token}" \
  --header 'Content-Type: application/json' \
  --header 'Accept: application/json, text/event-stream' \
  --data '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"log-scrubber","version":"1.0"}}}'

session_id="$(tr -d '\r' <"$headers_file" | awk -F': ' 'tolower($1)=="mcp-session-id" {print $2}')"
if [[ -z "$session_id" ]]; then
  echo "error: server did not return an Mcp-Session-Id header." >&2
  cat "$log_file" >&2
  exit 1
fi
session_header="Mcp-Session-Id: ${session_id}"

mcp_call '{"jsonrpc":"2.0","method":"notifications/initialized"}' --header "$session_header"
mcp_call '{"jsonrpc":"2.0","id":2,"method":"tools/list"}' --header "$session_header"
mcp_call '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"ping","arguments":{"message":"scrubber"}}}' --header "$session_header"

# Both credential shapes fail resolution; the failures are the log lines most
# likely to echo certificate material.
for ref in scrub-thumbprint scrub-path; do
  mcp_call "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"get-user-voice-config\",\"arguments\":{\"tenantId\":\"00000000-0000-0000-0000-0000000000aa\",\"credentialRef\":\"${ref}\",\"userUpn\":\"scrubber@example.invalid\"}}}" \
    --header "$session_header"
done

# Malformed input, to exercise the error/exception logging path.
mcp_call '{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"get-user-voice-config","arguments":{"tenantId":"not-a-guid"}}}' \
  --header "$session_header"

kill "$server_pid" 2>/dev/null || true
wait "$server_pid" 2>/dev/null || true
server_pid=""

log_bytes="$(wc -c <"$log_file" | tr -d ' ')"
echo "Captured ${log_bytes} bytes of host output."

# Guard against a vacuous scan: the run must actually have reached the code
# paths that handle certificate material.
for marker in \
  '"ping" completed' \
  'Could not resolve credential' \
  "Failed to load the certificate for credential 'scrub-path'"; do
  if ! grep --fixed-strings --quiet -- "$marker" "$log_file"; then
    echo "error: expected log marker not found: ${marker}" >&2
    echo "The run was not representative; the scan below would prove nothing." >&2
    cat "$log_file" >&2
    exit 1
  fi
done

# ---------------------------------------------------------------------------
# Scan.
# ---------------------------------------------------------------------------
scan_file() {
  local target="$1"
  local hits=0

  check_literal() {
    local label="$1" needle="$2" lines
    lines="$(grep --fixed-strings --ignore-case --line-number -- "$needle" "$target" | cut -d: -f1 | paste -sd, - || true)"
    if [[ -n "$lines" ]]; then
      echo "  HIT: ${label} (line ${lines})"
      hits=$((hits + 1))
    fi
  }

  check_pattern() {
    local label="$1" pattern="$2" lines
    lines="$(grep --extended-regexp --line-number -- "$pattern" "$target" | cut -d: -f1 | paste -sd, - || true)"
    if [[ -n "$lines" ]]; then
      echo "  HIT: ${label} (line ${lines})"
      hits=$((hits + 1))
    fi
  }

  check_literal "seeded certificate thumbprint" "$seeded_thumbprint"
  check_literal "seeded PFX password" "$seeded_pfx_password"
  check_literal "bearer token" "$bearer_token"
  check_literal "seeded private key body" "${seeded_key_body:0:64}"
  check_pattern "PEM private key marker" '-----BEGIN [A-Z ]*PRIVATE KEY-----'
  check_pattern "PFX/DER base64 key marker" 'MII[A-Za-z0-9+/]{40,}'
  check_pattern "bare 40-character hex thumbprint" '(^|[^A-Za-z0-9])[A-Fa-f0-9]{40}([^A-Za-z0-9]|$)'
  check_pattern "JWT-shaped value" 'eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.'

  return "$hits"
}

expected_checks=8

# Self-test: the scanner must detect every seeded pattern when it is present,
# otherwise a broken pattern would turn the gate into a silent pass.
canary="$workdir/canary.log"
{
  echo "thumbprint ${seeded_thumbprint}"
  echo "password ${seeded_pfx_password}"
  echo "token ${bearer_token}"
} >"$canary"
write_seeded_pem "$canary"
echo "jwt eyJhbGciOiJSUzI1NiJ9.eyJhdWQiOiJzY3J1YmJlciJ9.c2ln" >>"$canary"

echo "Scanner self-test:"
canary_hits=0
scan_file "$canary" || canary_hits=$?
if [[ "$canary_hits" -ne "$expected_checks" ]]; then
  echo "error: the scanner matched ${canary_hits}/${expected_checks} seeded patterns in the canary." >&2
  echo "At least one detection pattern is broken; the gate cannot be trusted." >&2
  exit 1
fi

echo "Scanning host output:"
log_hits=0
scan_file "$log_file" || log_hits=$?
if [[ "$log_hits" -gt 0 ]]; then
  echo "" >&2
  echo "Log scrubber gate failed: ${log_hits} secret pattern(s) reached host output." >&2
  exit 1
fi

echo "Log scrubber gate passed: 0/${expected_checks} secret patterns in ${log_bytes} bytes of host output."
