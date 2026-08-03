# teamsphone-mcp

[![CI](https://github.com/mcarlson501/teamsphone-mcp/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/mcarlson501/teamsphone-mcp/actions/workflows/ci.yml)

> [!WARNING]
> **Experimental and not ready for production use.** Do not rely on this project to
> make real Teams Phone changes. It can now connect to a Microsoft 365 tenant with
> your own Entra application and certificate, but interfaces, manifests, and audit
> formats are still changing.

`teamsphone-mcp` is an experimental, open-source **Model Context Protocol (MCP)
server** for building safe, deterministic Microsoft Teams Phone administration
tools. The long-term goal is to expose enumerated Move / Add / Change / Delete
(MACD), read, and diagnostic operations without providing arbitrary command
execution.

The repository is tenant-agnostic and contains no customer data or baked-in
credentials. Tenant credentials are supplied entirely through local configuration.

> **Project status:** Milestones M1–M5.5 are complete. Interfaces, manifests, and
> configuration may change without backward compatibility before the first release.

## What works today

- Streamable HTTP and stdio MCP transports.
- Fail-closed bearer authentication for HTTP and correlation-aware request logging.
- Strict YAML manifests with startup schema and annotation parity checks.
- Raw tool-argument validation before C# binding.
- Risk tiers, blast-radius checks, dry-run defaults, and HMAC confirmation tokens.
- Server and per-session what-if ceilings that force simulation, mark results with
  `simulated: true`, and never issue an execution token.
- An offline-tested tenant session manager with immutable tenant/credential context,
  read/write coordination, idle expiry, LRU eviction, and fatal-session replacement.
- A read-only `ping` tool and `mock-write-user-policy` safety-flow demonstration.
- Ten Phase A read-only Teams Phone tools, each with its own Pester suite.
- User voice diagnosis, recursive AA/CQ call-flow tracing, and a severity-ranked
  tenant health check.
- Dial-plan testing, call-queue health, orphan discovery, merged PSTN usage, and
  per-user call-quality diagnostics with actionable findings.
- Number, license, emergency-coverage, policy-assignment, and change-history reports.
- All ten Phase D write tools: eight atomic user-level operations and the
  `onboard-voice-user` / `offboard-voice-user` composites, each exercising the safe
  stage pipeline (snapshot → preflight → dry-run → execute → verify → rollback) — see
  [`docs/write-tools.md`](./docs/write-tools.md).
- A local JSONL audit trail with parameter redaction, correlation ids, snapshot
  storage, and a retention sweeper — see [`docs/audit.md`](./docs/audit.md).
- Tenant-scoped local audit query, change-detail, and Markdown/CSV export tools with
  signed, filter-bound pagination.
- Release build and test coverage in GitHub Actions.

## Not implemented yet

- Phase E shared-object writes.
- Container packaging or a supported release artifact.
- Hash-chained audit records and OpenTelemetry export (planned post-v1).

See [`teamsphone-mcp-build-spec`](./teamsphone-mcp-build-spec) for the milestone
roadmap. Issues and focused contributions are welcome; review
[`CONTRIBUTING.md`](./CONTRIBUTING.md) before proposing implementation work.

## Phase A tools (read-only, tier 0)

| Tool | What it returns |
| ---- | --------------- |
| `get-user-voice-config` | A user's voice configuration — the **reference implementation** for new tools |
| `get-tenant-voice-snapshot` | Composite overview: numbers, resource accounts, call queues, auto attendants, emergency locations, schedules, policy counts |
| `list-phone-numbers` | Tenant phone numbers with assignment state (paged) |
| `get-callqueue-config` | One call queue's routing, agents, and overflow/timeout handling |
| `get-autoattendant-config` | One auto attendant's call flows, menus, and targets |
| `check-user-licensing` | Voice-relevant license and feature plan state for a user |
| `list-emergency-addresses` | Emergency locations and validation state (paged) |
| `list-voice-policies` | Voice routing, dial plan, calling, and voicemail policies |
| `list-resource-accounts` | Resource accounts and what they are attached to (paged) |
| `get-schedules` | Schedules and the auto attendants referencing them (paged) |

Each tool is a `manifest.yaml` + `run.ps1` pair under [`tools/`](./tools) with its own
Pester suite; adding a tool never requires editing the host engine.

## Diagnostics and reports (tier 0)

| Tool | What it returns |
| ---- | --------------- |
| `diagnose-user-voice` | Ordered license, enterprise voice, number, policy, dial-plan, and emergency-location findings with fixes |
| `trace-call-flow` | A number-to-resource-account AA/CQ graph with agents, terminal targets, loop detection, and broken-reference findings |
| `test-dialplan-number` | Effective dial-plan normalization and the rule that matched a dialed string for a user |
| `diagnose-callqueue-health` | Agent opt-in, identity resolution, presence starvation, and overflow/timeout target findings |
| `get-pstn-usage` | Calling Plan, Operator Connect, and Direct Routing call rows, totals, costs, and failure findings for up to 90 days (`fromUtc` inclusive, `toUtc` exclusive) |
| `get-call-quality-summary` | Up to 30 days of per-user packet loss, jitter, round-trip time, degradation, and concealment evidence from Graph call records (`fromUtc` inclusive, `toUtc` exclusive) |
| `find-orphaned-objects` | Broken AA targets, incomplete resource accounts/users, empty queues, and unused custom policies |
| `run-tenant-health-check` | Severity-ranked findings across capacity, licensing, emergency coverage, orphan discovery, and full queue health |
| `report-number-utilization` | Assignment and availability totals by number type and country |
| `report-license-utilization` | Observed Phone System/Calling Plan assignments and licensed users not enabled for voice |
| `report-emergency-coverage` | Enterprise-voice users with covered, missing, unknown, or unvalidated locations (paged) |
| `report-policy-assignments` | User-by-policy matrix as structured data, Markdown, or CSV (paged) |
| `report-change-history` | Markdown or CSV generated from this server's tenant-scoped local audit trail |

## Local audit tools (tier 0)

| Tool | What it returns |
| ---- | --------------- |
| `query-audit-log` | Filtered, newest-first local audit records with signed continuation tokens |
| `get-change-detail` | One full record plus available before/after snapshots |
| `export-audit-report` | Markdown or CSV for a tenant and UTC period |

These tools read only the configured local audit root and never open a tenant
PowerShell session. `report-change-history` is the report-oriented alias of the same
secure export path.

## Write tools (MACD)

| Tool | Tier | What it does |
| ---- | ---- | ------------ |
| `assign-phone-number` | 1 | Enables enterprise voice, assigns an available number, and optionally applies existing policies and a validated emergency location |
| `remove-phone-number` | 2 | Releases a user's number to tenant inventory while preserving restorable metadata |
| `move-number-between-users` | 2 | Releases an assigned phone number from one user and assigns it to another, with preflight gating, verification, and automatic rollback |
| `onboard-voice-user` | 2 | Composes number, policy, caller ID, voicemail, and emergency-location setup with reverse compensation |
| `offboard-voice-user` | 2 | Removes queue memberships, releases the number, clears policies, disables enterprise voice, and reports disposition |
| `update-callqueue-members` | 1 | Replaces one queue's direct user membership |
| `update-user-calling-policies` | 1 | Assigns existing voice routing, dial plan, and Teams calling policies |
| `update-user-voicemail-settings` | 1 | Updates per-user voicemail and out-of-office greeting behavior |
| `set-caller-id-assignment` | 1 | Assigns an existing caller ID policy |
| `update-user-emergency-location` | 2 | Assigns an existing validated, rollback-safe emergency location |

Write tools are dry-run by default and require an HMAC confirmation token issued by a
prior dry-run before anything is changed. See
[`docs/write-tools.md`](./docs/write-tools.md) for the full protocol and for how to
author one.

## Layout

```
src/TeamsPhoneMcp.Host/   ASP.NET Core entrypoint, transports, auth + logging middleware
src/TeamsPhoneMcp.Core/   Tools, strict manifest catalog, and policy boundary
src/TeamsPhoneMcp.Audit/  JSONL audit sink, redaction, snapshots, retention sweeper
tools/                    One validated manifest per exposed tool
tests/unit/               xUnit unit and host-level MCP acceptance tests
```

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- (For manual testing) [MCP Inspector](https://github.com/modelcontextprotocol/inspector):
  `npx @modelcontextprotocol/inspector`

## Build & test

```bash
dotnet build TeamsPhoneMcp.sln
dotnet test  TeamsPhoneMcp.sln
pwsh -NoProfile -c "Invoke-Pester -Path tools"
```

## Development quickstart

These commands run the local development host. Listing tools does not connect to
Microsoft 365; calling a tenant tool requires a configured credential, and confirmed
write calls can change Teams Phone state.

The host selects its transport from the command line / environment:

| Transport            | How to select                                  | Auth                         |
| -------------------- | ---------------------------------------------- | ---------------------------- |
| Streamable HTTP (default) | *(no flag)* — serves `/mcp`               | Static token **required**    |
| stdio                | `--stdio` **or** `TEAMSPHONE_MCP_STDIO=true`   | Locally trusted (no token)   |

### Configuration

| Setting                        | Env var / config key                          | Purpose                                   |
| ------------------------------ | --------------------------------------------- | ----------------------------------------- |
| Client auth token (HTTP)       | `TEAMSPHONE_MCP_BEARER_TOKEN` (or `Auth:BearerToken`) | Static token clients must present    |
| Confirmation token signing key | `TEAMSPHONE_MCP_CONFIRMATION_TOKEN_KEY` (or `Policy:ConfirmationTokenKey`) | Base64 key to keep dry-run confirmation tokens valid across restarts |
| Server mode ceiling             | `TEAMSPHONE_MCP_MODE` (or `ServerMode`) | `full` (default), `whatif`, or `readonly` |
| Tool manifest root             | `ToolManifests__ToolsRootPath` (or `ToolManifests:ToolsRootPath`) | Optional manifest directory override |
| Session idle timeout           | `TenantSessions__IdleTimeout` (or `TenantSessions:IdleTimeout`) | Inactive session lifetime; default `00:10:00` |
| Maximum tenant sessions        | `TenantSessions__MaxSessions` (or `TenantSessions:MaxSessions`) | Live session cap; default `10` |
| Session cleanup interval       | `TenantSessions__CleanupInterval` (or `TenantSessions:CleanupInterval`) | Idle-session scan interval; default `00:01:00` |
| Audit trail                    | `Audit__Enabled`, `Audit__RootPath`, `Audit__RetentionDays`, `Audit__SweepIntervalHours` | JSONL audit storage; see [`docs/audit.md`](./docs/audit.md) |
| Transport = stdio              | `TEAMSPHONE_MCP_STDIO=true` (or `--stdio`)    | Use stdio instead of HTTP                 |
| HTTP bind address              | `ASPNETCORE_URLS`                             | e.g. `http://127.0.0.1:5199`              |

The bearer token is **read from configuration/environment only** — it is never
hardcoded and never written to logs. If no token is configured, the HTTP transport
**fails closed**: every request to `/mcp` is rejected with `401`.

By default, manifests load from `tools/` beside the built or published host. A
configured relative manifest path resolves against the host content root; an absolute
path is used as supplied. Startup fails before listening if a manifest is missing,
invalid, orphaned, or inconsistent with its C# tool schema or annotations.

Every raw `tools/call` argument set is validated against its manifest before C#
binding. Unknown or missing fields, JSON type mismatches, and invalid declared
formats are rejected without invoking the tool.

### HTTP transport

```bash
export TEAMSPHONE_MCP_BEARER_TOKEN='choose-a-strong-token'
export ASPNETCORE_URLS='http://127.0.0.1:5199'
dotnet run --project src/TeamsPhoneMcp.Host
```

### stdio transport

```bash
dotnet run --project src/TeamsPhoneMcp.Host -- --stdio
```

## Connecting with MCP Inspector (manual acceptance harness)

### Over HTTP

1. Start the HTTP server as above (with a bearer token set).
2. Launch Inspector: `npx @modelcontextprotocol/inspector`.
3. Choose transport **Streamable HTTP**, URL `http://127.0.0.1:5199/mcp`.
4. Under **Authentication**, add an `Authorization` header using the `Bearer`
   scheme followed by your configured token.
5. Connect, then **List Tools** → you should see the 38 registered tools, including
  `ping`, the tenant reads/diagnostics/reports, local audit tools, and Phase D writes.
6. Call `mock-write-user-policy` once without `dryRun:false` to get a
   `confirmationToken`, then call again with `dryRun:false` and that token to
   execute the mocked write. Changing `targetUserUpn`, `policyName`, or
   `blastRadius` requires a new dry-run token.

### Over stdio

1. Launch Inspector: `npx @modelcontextprotocol/inspector`.
2. Choose transport **STDIO** with:
   - Command: `dotnet`
   - Arguments: `run --project src/TeamsPhoneMcp.Host -- --stdio`
3. Connect, then **List Tools** → `ping` and `mock-write-user-policy`.

## Verifying the unauthenticated rejection (acceptance criterion)

With the HTTP server running and a token configured, an unauthenticated request to
`/mcp` must return `401` with no tool listing:

```bash
curl -s -o /dev/null -w '%{http_code}\n' -X POST http://127.0.0.1:5199/mcp \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}'
# → 401
```

## Next milestone

M6 hardens packaging, documentation, CI, and the first supported container release.

## License

[Apache 2.0](./LICENSE).
