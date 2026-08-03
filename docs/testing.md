# Testing TeamsPhone MCP

This guide covers every way to test the server, from fast automated checks that need
no tenant to a full live call against a real Microsoft 365 tenant. Work top-to-bottom:
each layer is faster and more isolated than the one below it.

| Layer | What it proves | Needs a tenant? | Speed |
| ----- | -------------- | --------------- | ----- |
| 1. .NET unit/acceptance tests | Wiring, manifests, policy, audit records, fail-closed paths | No | Fast |
| 2. PowerShell (Pester) tests | Each tool's `run.ps1` stage logic | No | Fast |
| 3. Local server smoke test | The host boots, lists tools, enforces auth | No | Seconds |
| 4. Live end-to-end | A real tool call against your tenant | **Yes** | Slow |

> **Golden rule:** always run against a **fresh build**. A stale `bin/Release` output
> can contain an older set of tools. Run `dotnet build TeamsPhoneMcp.sln` first, and when
> running a compiled DLL directly, use the build configuration you just built (Debug by
> default).

---

## Prerequisites

- **.NET 8 SDK** (pinned via `global.json`).
- **PowerShell 7.4+** (`pwsh`) — for Pester and the runspace executor.
- **Pester 5+** — install once with `pwsh -c "Install-Module Pester -Scope CurrentUser"`.
- **MicrosoftTeams module** (only for Layer 4) — `pwsh -c "Install-Module MicrosoftTeams -Scope CurrentUser"`.
- **A configured `credentialRef`** (only for Layer 4) — see [setup-entra-app.md](setup-entra-app.md).

---

## Layer 1 — .NET unit and acceptance tests

The primary safety net. Covers manifest/schema parity, argument validation, write policy,
the confirmation-token service, pagination and continuation tokens, the audit pipeline,
session lifecycle, and the MCP host end to end (including the fail-closed path when a
credential is not configured).

```bash
# Build first (warnings are errors — keep it clean).
dotnet build TeamsPhoneMcp.sln

# Run the complete offline suite even if live-test variables are exported.
dotnet test TeamsPhoneMcp.sln --filter 'FullyQualifiedName!~IntegrationTests'

# Run everything, including gated live tests when their variables are present.
dotnet test TeamsPhoneMcp.sln
```

Useful variations:

```bash
# Just the unit test project.
dotnet test tests/unit/TeamsPhoneMcp.UnitTests.csproj

# A single test by name (substring match).
dotnet test tests/unit --filter FullyQualifiedName~ListTools_ExposesManifestParityContracts

# Fail-closed check: a manifest tool returns a clean Failed envelope with no secret leak.
dotnet test tests/unit --filter FullyQualifiedName~CallTool_ManifestPipelineTool_FailsClosedWithoutConfiguredCredential

# Audit trail: redaction, the JSONL sink, retention, and end-to-end record writing.
dotnet test tests/unit --filter FullyQualifiedName~Audit

# Write pipeline: dry-run → confirm → execute → verify, and the forced rollback path.
dotnet test tests/unit --filter FullyQualifiedName~WritePipelineAcceptanceTests
```

The offline-filtered suite uses **no tenant and no credentials** — the fail-closed test deliberately calls
`get-user-voice-config` with an unconfigured credential and asserts an `authenticationFailed`
envelope whose client-facing message contains no credential reference.

---

## Layer 2 — PowerShell (Pester) tests

Each tool ships a `run.Tests.ps1` next to its `run.ps1`. These stub the Teams cmdlets and
assert the stage logic (execute path, not-found handling, unsupported-stage rejection, the
pagination contract for paged tools, and the single-JSON-line output contract). Shared
helpers in `tools/common/TeamsPhoneMcp.Common.psm1` have their own suite.

```bash
# One tool.
pwsh -NoProfile -c "Invoke-Pester -Path tools/get-user-voice-config/run.Tests.ps1 -Output Detailed"

# All tool tests.
pwsh -NoProfile -c "Invoke-Pester -Path tools -Output Detailed"
```

No tenant required — the Teams cmdlets are stubbed inside `BeforeAll`.

---

## Layer 3 — Local server smoke test (no tenant)

Confirms the host boots, loads the tool manifests, validates them against the registered
tools, and enforces bearer auth — all without connecting to a tenant. A tool *call* would
require credentials, but `initialize` and `tools/list` do not.

### 3a. stdio transport (local, no bearer token)

```bash
dotnet run --project src/TeamsPhoneMcp.Host -- --stdio
```

The server reads JSON-RPC from stdin and writes to stdout; logs go to stderr. This is the
mode a local MCP client (VS Code, Claude Desktop) uses. Point your client at the command
above and confirm it lists `get-user-voice-config`, `mock-write-user-policy`, and `ping`.

> Piping requests with `printf ... | dotnet run` is unreliable: closing stdin makes the
> host shut down before it flushes responses. Use a real MCP client, the HTTP smoke test
> below, or the Layer 1 acceptance tests instead.

### 3b. HTTP transport (bearer-token protected)

```bash
# A dev token is required or every /mcp request is rejected with 401.
export TEAMSPHONE_MCP_BEARER_TOKEN='dev-local-token'
export ASPNETCORE_URLS='http://localhost:5111'
dotnet run --project src/TeamsPhoneMcp.Host
```

On startup you should see `Loaded 33 tool manifests` and `Validated 33 tool manifests
against 33 registered MCP tools`. In another terminal, verify the auth gate:

```bash
# No token → 401.
curl -s -o /dev/null -w "%{http_code}\n" -X POST http://localhost:5111/mcp \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"c","version":"1"}}}'

# Correct token → 200 with an Mcp-Session-Id response header.
curl -s -D - -o /dev/null -X POST http://localhost:5111/mcp \
  -H 'Authorization: Bearer dev-local-token' \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"c","version":"1"}}}'
```

If the manifest count is lower than the number of folders under `tools/` (excluding
`_template` and `common`), you are running a stale build. Rebuild, or run the DLL from the
configuration you just built.

---

## Layer 4 — Live end-to-end against a real tenant

This is the only layer that actually connects to Microsoft 365. It resolves your
`credentialRef`, unlocks the certificate, runs `Connect-MicrosoftTeams` in app-only mode,
and executes the tool's cmdlets. Complete [setup-entra-app.md](setup-entra-app.md) first.

**Preflight — verify the certificate loads with your password** (no tenant call):

```bash
PFX=~/.config/teamsphone-mcp/teamsphone-mcp-dev.pfx
openssl pkcs12 -in "$PFX" -passin pass:"$TEAMSPHONE_MCP_DEV_PFX_PASSWORD" -nokeys -clcerts 2>/dev/null \
  | openssl x509 -noout -subject -enddate -fingerprint -sha1
```

The printed SHA-1 fingerprint must match the certificate uploaded to your Entra app under
**Certificates & secrets**.

### Option A — the gated integration test (recommended)

`tests/unit/GetUserVoiceConfigIntegrationTests.cs` drives a real MCP call through an
in-process HTTP host. It **skips cleanly** unless the three `IT` environment variables are
set, so it never breaks the default `dotnet test` run.

```bash
# The PFX password the server uses to unlock the certificate.
export TEAMSPHONE_MCP_DEV_PFX_PASSWORD='<your pfx export password>'

# Integration inputs (synthetic-data rule still applies to anything you commit).
export TEAMSPHONE_MCP_IT_TENANT_ID='<your directory tenant id>'
export TEAMSPHONE_MCP_IT_CREDENTIAL_REF='dev-tenant'
export TEAMSPHONE_MCP_IT_USER_UPN='<a real user upn in that tenant>'

# Development env so the host loads the Credentials from appsettings.Development.json.
ASPNETCORE_ENVIRONMENT=Development \
  dotnet test tests/unit \
  --filter FullyQualifiedName~GetUserVoiceConfig_ReturnsConfiguration_WhenTenantConfigured
```

A pass means the tool returned a `Succeeded` envelope whose
`diff.after.userPrincipalName` equals the UPN you queried.

### Option A2 — the full Phase A live verification (M3 sign-off)

`tests/unit/PhaseAIntegrationTests.cs` calls **every Phase A read tool** once against the
dev tenant, asserts each result envelope, and then asserts that the audit trail recorded
every call with no credential material. This is the M3 acceptance run. It uses the same
gating variables and also skips cleanly when they are unset.

Keep tenant values out of tracked files — copy `.env.integration.template` to a
gitignored `.env.integration` and fill it in. The credential keys the host reads contain
a hyphen (`Credentials__dev-tenant__ClientId`), which no shell can export as a variable,
so the file holds shell-safe `TP_CRED_*` names and `scripts/live-test.sh` translates them
with `env(1)`:

```bash
# .env.integration  (gitignored — never commit real tenant values)
TEAMSPHONE_MCP_DEV_PFX_PASSWORD='<your pfx export password>'
TP_CRED_TENANT_ID='<directory tenant id>'
TP_CRED_CLIENT_ID='<application (client) id>'
TP_CRED_CERT_PATH="$HOME/.config/teamsphone-mcp/teamsphone-mcp-dev.pfx"

TEAMSPHONE_MCP_IT_TENANT_ID='<directory tenant id>'
TEAMSPHONE_MCP_IT_CREDENTIAL_REF='dev-tenant'
TEAMSPHONE_MCP_IT_USER_UPN='<a real user upn in that tenant>'

# Optional — when unset, the test discovers a call queue and an auto attendant from
# the tenant's resource accounts, and skips whichever object type does not exist.
TEAMSPHONE_MCP_IT_CALL_QUEUE='<a call queue name or guid>'
TEAMSPHONE_MCP_IT_AUTO_ATTENDANT='<an auto attendant name or guid>'
```

```bash
set -a; source .env.integration; set +a
./scripts/live-test.sh --filter FullyQualifiedName~PhaseAIntegration -l 'console;verbosity=detailed'
```

The test prints each tool's `summary` line, so a pass doubles as a readable snapshot of the
dev tenant. It fails if any tool returns a non-`Succeeded` envelope, if the number of audit
records does not match the number of calls, or if the audit text matches a private-key,
thumbprint, or JWT shape.

### Option A3 — the live write-pipeline demo (M4 sign-off)

`tests/unit/MoveNumberIntegrationTests.cs` drives `move-number-between-users` against
the dev tenant: dry-run → confirmation token → confirmed execute → verification, then
moves the number **back** to the source user so the tenant is left as it was found. It
asserts four audit records (two dry runs, two executes) with both snapshots stored for
each real change, and no credential material anywhere in the trail.

Tenant prerequisites — the tool's preflight enforces all of these:

- the **source** user currently holds the phone number,
- the **target** user is licensed for Phone System and has **no** number assigned,
- the number appears in the tenant's number inventory (`Get-CsPhoneNumberAssignment`).

```bash
# In addition to the shared TEAMSPHONE_MCP_IT_TENANT_ID / _CREDENTIAL_REF variables:
TEAMSPHONE_MCP_IT_MOVE_SOURCE_UPN='<user who holds the number>'
TEAMSPHONE_MCP_IT_MOVE_TARGET_UPN='<voice-licensed user with no number>'
TEAMSPHONE_MCP_IT_MOVE_NUMBER='+15551234567'   # optional; defaults to the source user's number

# Optional second test: a target the tenant cannot accept (a resource account, or a
# user without the licence the number type requires). Asserts the move is blocked by
# preflight with no token issued. This test writes nothing.
TEAMSPHONE_MCP_IT_MOVE_INELIGIBLE_TARGET_UPN='<a resource account upn>'
```

```bash
set -a; source .env.integration; set +a
./scripts/live-test.sh --filter FullyQualifiedName~MoveNumberIntegration -l 'console;verbosity=detailed'
```

> This test **writes to your tenant**. It only ever touches the two users you name, and
> `maxBlastRadius: 1` prevents it from affecting anything else. Replication lag is
> absorbed by the verify stage's bounded polling.

### Option A4 — the Phase D composite round trip (M5 sign-off)

`tests/unit/PhaseDIntegrationTests.cs` dry-runs and confirms
`offboard-voice-user`, then restores the captured state through a dry-run and confirmed
`onboard-voice-user` call in a `finally` block. It verifies the numbered user and tenant
inventory match the baseline and that all four write calls were audited.

Use an isolated fixture that meets every prerequisite:

- enterprise voice is enabled and a phone number is assigned;
- the number has an existing validated emergency location;
- the user has no direct call queue memberships and no caller ID policy;
- no other test or administrator changes the fixture during the run.

```bash
# Optional. Defaults to TEAMSPHONE_MCP_IT_MOVE_SOURCE_UPN when unset.
TEAMSPHONE_MCP_IT_PHASE_D_USER_UPN='<isolated numbered user>'

set -a; source .env.integration; set +a
./scripts/live-test.sh --filter FullyQualifiedName~PhaseDIntegrationTests -l 'console;verbosity=detailed'
```

> This test writes to the tenant twice. Do not use a clean Calling Plan number without
> an emergency location as the fixture: Calling Plan assignment cannot be restored
> without one. If restoration fails, preserve the failing envelope and restore from
> its `diff.before` snapshot before running another live write test.

### Option A5 — diagnostics and reporting (M5.5 sign-off)

`tests/unit/M55IntegrationTests.cs` calls the user diagnostic, tenant health check,
number/license/emergency/policy reports, and call-flow trace against the dev tenant. It
asserts actionable findings and one scrubbed audit record per call. The test discovers a
numbered, attached resource account automatically; set an explicit number only when the
tenant has multiple call flows and you need a stable fixture.

The configured user and tenant should contain at least one deliberate voice issue so the
test can verify remediation text rather than only an empty healthy result.

```bash
# Optional E.164 override for trace-call-flow discovery.
TEAMSPHONE_MCP_IT_DIALED_NUMBER='+15551234567'

set -a; source .env.integration; set +a
./scripts/live-test.sh --filter FullyQualifiedName~M55IntegrationTests -l 'console;verbosity=detailed'
```

### Option B — a real MCP client against the running server

Start the server (stdio per 3a, or HTTP per 3b with a bearer token and the PFX password
exported), connect an MCP client, and call:

```jsonc
{
  "tenantId": "<your directory tenant id>",
  "credentialRef": "dev-tenant",
  "userUpn": "<a real user upn>"
}
```

---

## Interpreting the result envelope

Every tool returns a structured envelope. The `status` field is **PascalCase**:

| `status` | Meaning |
| -------- | ------- |
| `Succeeded` | The tool ran and returned data (see `diff.after`). |
| `DryRunCompleted` | A write tool previewed its change without applying it. |
| `PreflightFailed` | A safety check failed; nothing was attempted, and no token was issued. |
| `RolledBack` | The change failed and the original state was restored. |
| `VerifyFailedRolledBack` | The change applied but verification failed, so it was rolled back. |
| `Failed` | Something went wrong; see the `error` object. |

On failure, `error.code` tells you what to fix. `authenticationFailed` is intentionally
generic and never echoes your `credentialRef` or any secret back to the client.

---

## Checking the audit trail

Every accepted call — including forced failures — writes one JSONL record under the
configured audit root. After a Layer 3 or Layer 4 run:

```bash
find audit -name '*.jsonl' -exec cat {} \; | tail -1 | python3 -m json.tool
```

The record's `correlationId` matches the one in the client-facing envelope, and
`parameters` must never contain a secret. See [audit.md](audit.md) for the full schema,
redaction rules, and retention behaviour.

---

## Troubleshooting

| Symptom | Likely cause / fix |
| ------- | ------------------ |
| Fewer manifests loaded than `tools/` folders | Stale build. Rebuild and run the current configuration's DLL. |
| No files under `audit/` after a call | `Audit:Enabled` is `false`, or the process cannot write to `Audit:RootPath` (check the host's error logs — audit failures never fail the call). |
| Every `/mcp` request returns `401` | No `TEAMSPHONE_MCP_BEARER_TOKEN` set (HTTP mode). Set one, or use stdio. |
| `The tenant credential could not be resolved` | Wrong PFX path/password, missing private key, or `CertificatePasswordEnvVar` not exported. Re-run the certificate preflight above. |
| `credential does not belong to the requested tenant` | The `tenantId` argument doesn't match `TenantId` in the `credentialRef` config entry. |
| `authenticationFailed` on a live call | Certificate not uploaded/consented, missing Graph permission, or the app lacks the Teams admin role. See [setup-entra-app.md](setup-entra-app.md) steps 2 and 4. |
| Pester `Invoke-Pester` not found | `Install-Module Pester -Scope CurrentUser`. |
| `Connect-MicrosoftTeams` not found on a live call | `Install-Module MicrosoftTeams -Scope CurrentUser`. |
| The integration test always passes instantly | It skips when any `TEAMSPHONE_MCP_IT_*` variable is unset — that's expected. Set all three. |
| A plain `dotnet test` suddenly fails the integration tests | The `TEAMSPHONE_MCP_IT_*` variables are still exported in that shell, so the gated tests run without the credential configuration `scripts/live-test.sh` supplies. Open a new shell, or unset them. |
