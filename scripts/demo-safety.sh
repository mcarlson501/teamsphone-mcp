#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
image="${TEAMSPHONE_MCP_DEMO_IMAGE:-teamsphone-mcp:local}"
port="${TEAMSPHONE_MCP_DEMO_PORT:-58198}"
tenant_id="11111111-1111-1111-1111-111111111111"
container_name="teamsphone-mcp-safety-$$"
audit_volume="teamsphone-mcp-safety-audit-$$"
work_dir="$(mktemp -d)"

cleanup() {
  docker rm --force "$container_name" >/dev/null 2>&1 || true
  docker volume rm "$audit_volume" >/dev/null 2>&1 || true
  rm -rf "$work_dir"
}
trap cleanup EXIT INT TERM

for command in docker curl jq openssl; do
  if ! command -v "$command" >/dev/null 2>&1; then
    echo "error: $command is required." >&2
    exit 1
  fi
done

step() {
  printf '\n==> %s\n' "$1"
}

assert_json() {
  local json="$1"
  local expression="$2"
  local message="$3"
  if ! jq -e "$expression" >/dev/null <<<"$json"; then
    echo "error: $message" >&2
    exit 1
  fi
}

extract_json() {
  local body_file="$1"
  if jq -e . "$body_file" >/dev/null 2>&1; then
    cat "$body_file"
    return
  fi

  local data_line
  data_line="$(grep '^data:' "$body_file" | head -n 1 || true)"
  if [[ -z "$data_line" ]]; then
    echo "error: MCP response contained neither JSON nor SSE data." >&2
    exit 1
  fi
  printf '%s\n' "${data_line#data: }"
}

mcp_post() {
  local label="$1"
  local session_id="$2"
  local payload="$3"
  local headers_file="$work_dir/$label.headers"
  local body_file="$work_dir/$label.body"
  local curl_args=(
    --silent --show-error
    --dump-header "$headers_file"
    --output "$body_file"
    --write-out '%{http_code}'
    --request POST "http://127.0.0.1:$port/mcp"
    --header "Authorization: Bearer $bearer_token"
    --header 'Content-Type: application/json'
    --header 'Accept: application/json, text/event-stream'
    --data "$payload"
  )
  if [[ -n "$session_id" ]]; then
    curl_args+=(
      --header "Mcp-Session-Id: $session_id"
      --header 'MCP-Protocol-Version: 2025-11-25'
    )
  fi

  local status_code
  status_code="$(curl "${curl_args[@]}")"
  if [[ "$status_code" != 200 && "$status_code" != 202 ]]; then
    echo "error: MCP request '$label' returned HTTP $status_code." >&2
    exit 1
  fi

  if [[ ! -s "$body_file" ]]; then
    return
  fi

  extract_json "$body_file"
}

initialize_session() {
  local label="$1"
  local what_if_mode="$2"
  local payload
  payload="$(jq -cn \
    --arg id "$label-initialize" \
    --argjson whatIfMode "$what_if_mode" \
    '{jsonrpc:"2.0", id:$id, method:"initialize", params:{protocolVersion:"2025-11-25", capabilities:{}, clientInfo:{name:"teamsphone-safety-demo", version:"0.1.0"}, _meta:{whatIfMode:$whatIfMode}}}')"
  mcp_post "$label-initialize" "" "$payload" >/dev/null

  local session_id
  session_id="$(grep -i '^mcp-session-id:' "$work_dir/$label-initialize.headers" | cut -d: -f2- | tr -d ' \r')"
  if [[ -z "$session_id" ]]; then
    echo "error: initialize returned no Mcp-Session-Id." >&2
    exit 1
  fi

  payload="$(jq -cn '{jsonrpc:"2.0", method:"notifications/initialized", params:{}}')"
  mcp_post "$label-initialized" "$session_id" "$payload" >/dev/null
  printf '%s\n' "$session_id"
}

call_tool() {
  local label="$1"
  local session_id="$2"
  local tool_name="$3"
  local arguments="$4"
  local payload
  payload="$(jq -cn \
    --arg id "$label" \
    --arg name "$tool_name" \
    --argjson arguments "$arguments" \
    '{jsonrpc:"2.0", id:$id, method:"tools/call", params:{name:$name, arguments:$arguments}}')"

  local response
  response="$(mcp_post "$label" "$session_id" "$payload")"
  assert_json "$response" 'has("error") | not' "tool call '$tool_name' returned a JSON-RPC error"
  jq -c '.result.structuredContent // (.result.content[0].text | fromjson)' <<<"$response"
}

step "Build the local image"
if [[ "${TEAMSPHONE_MCP_DEMO_SKIP_BUILD:-false}" != true ]]; then
  docker build --quiet --tag "$image" "$repo_root" >/dev/null
elif ! docker image inspect "$image" >/dev/null 2>&1; then
  echo "error: $image does not exist and TEAMSPHONE_MCP_DEMO_SKIP_BUILD=true." >&2
  exit 1
fi

bearer_token="$(openssl rand -hex 32)"
signing_key="$(openssl rand -base64 32 | tr -d '\n')"
docker volume create "$audit_volume" >/dev/null

step "Start an isolated full-mode server with synthetic data"
docker run \
  --detach \
  --name "$container_name" \
  --publish "127.0.0.1:$port:8080" \
  --env "TEAMSPHONE_MCP_BEARER_TOKEN=$bearer_token" \
  --env "TEAMSPHONE_MCP_CONFIRMATION_TOKEN_KEY=$signing_key" \
  --env TEAMSPHONE_MCP_MODE=full \
  --env Audit__RootPath=/data/audit \
  --volume "$audit_volume:/data/audit" \
  "$image" >/dev/null

for _ in {1..60}; do
  status_code="$(curl --silent --output /dev/null --write-out '%{http_code}' \
    --request POST "http://127.0.0.1:$port/mcp" || true)"
  if [[ "$status_code" == 401 ]]; then
    break
  fi
  if [[ "$(docker inspect --format '{{.State.Status}}' "$container_name")" == exited ]]; then
    echo "error: demo container exited during startup." >&2
    docker logs "$container_name" >&2
    exit 1
  fi
  sleep 1
done
if [[ "${status_code:-}" != 401 ]]; then
  echo "error: demo server did not become ready." >&2
  exit 1
fi

session_id="$(initialize_session full false)"

step "1. Dry-run is the default"
dry_run_arguments="$(jq -cn \
  --arg tenantId "$tenant_id" \
  '{tenantId:$tenantId, targetUserUpn:"demo.user@example.com", policyName:"DemoPolicy", blastRadius:1}')"
dry_run="$(call_tool dry-run "$session_id" mock-write-user-policy "$dry_run_arguments")"
assert_json "$dry_run" '.status == "dryRunCompleted" and .dryRun == true and .simulated == false' \
  "dry-run did not return the expected result"
assert_json "$dry_run" '.confirmationToken | type == "string" and length > 0' \
  "dry-run issued no confirmation token"
confirmation_token="$(jq -r '.confirmationToken' <<<"$dry_run")"
printf 'status=%s simulated=%s confirmationTokenIssued=true\n' \
  "$(jq -r '.status' <<<"$dry_run")" \
  "$(jq -r '.simulated' <<<"$dry_run")"

step "2. Changed parameters are refused"
changed_arguments="$(jq -cn \
  --arg tenantId "$tenant_id" \
  --arg token "$confirmation_token" \
  '{tenantId:$tenantId, targetUserUpn:"demo.user@example.com", policyName:"ChangedPolicy", blastRadius:1, dryRun:false, confirmationToken:$token}')"
refused="$(call_tool refused "$session_id" mock-write-user-policy "$changed_arguments")"
assert_json "$refused" '.status == "policyRejected" and .errorCode == "invalidConfirmationToken"' \
  "changed parameters were not refused"
printf 'status=%s errorCode=%s\n' \
  "$(jq -r '.status' <<<"$refused")" \
  "$(jq -r '.errorCode' <<<"$refused")"

step "3. The exact confirmed plan executes"
execute_arguments="$(jq -cn \
  --arg tenantId "$tenant_id" \
  --arg token "$confirmation_token" \
  '{tenantId:$tenantId, targetUserUpn:"demo.user@example.com", policyName:"DemoPolicy", blastRadius:1, dryRun:false, confirmationToken:$token}')"
executed="$(call_tool executed "$session_id" mock-write-user-policy "$execute_arguments")"
assert_json "$executed" '.status == "succeeded" and .dryRun == false and .simulated == false' \
  "confirmed execution did not succeed"
execution_correlation_id="$(jq -r '.correlationId' <<<"$executed")"
printf 'status=%s correlationId=%s\n' \
  "$(jq -r '.status' <<<"$executed")" \
  "$execution_correlation_id"

step "4. The execution is retrievable from the audit trail"
audit_arguments="$(jq -cn \
  --arg tenantId "$tenant_id" \
  '{tenantId:$tenantId, toolId:"mock-write-user-policy", pageSize:100}')"
audit_result="$(call_tool audit "$session_id" query-audit-log "$audit_arguments")"
assert_json "$audit_result" \
  ".records | any(.correlationId == \"$execution_correlation_id\" and .status == \"Succeeded\")" \
  "the execution correlation ID was not found in the audit trail"
printf 'auditMatch=true correlationId=%s\n' "$execution_correlation_id"

step "5. Session what-if cannot be promoted to execution"
what_if_session_id="$(initialize_session whatif true)"
what_if_arguments="$(jq -cn \
  --arg tenantId "$tenant_id" \
  '{tenantId:$tenantId, targetUserUpn:"demo.user@example.com", policyName:"DemoPolicy", blastRadius:1, dryRun:false}')"
simulated="$(call_tool simulated "$what_if_session_id" mock-write-user-policy "$what_if_arguments")"
assert_json "$simulated" \
  '.status == "dryRunCompleted" and .simulated == true and (.confirmationToken == null)' \
  "session what-if did not force tokenless simulation"
printf 'status=%s simulated=%s confirmationTokenIssued=false\n' \
  "$(jq -r '.status' <<<"$simulated")" \
  "$(jq -r '.simulated' <<<"$simulated")"

step "Safety demonstration passed"
echo "No Microsoft 365 tenant was contacted; all write behavior above used the synthetic mock tool."