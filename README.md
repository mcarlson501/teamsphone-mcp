# TeamsPhone MCP

[![CI](https://github.com/mcarlson501/teamsphone-mcp/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/mcarlson501/teamsphone-mcp/actions/workflows/ci.yml)
[![OpenSSF Scorecard](https://api.securityscorecards.dev/projects/github.com/mcarlson501/teamsphone-mcp/badge)](https://scorecard.dev/viewer/?uri=github.com/mcarlson501/teamsphone-mcp)
[![License](https://img.shields.io/badge/license-Apache%202.0-blue.svg)](./LICENSE)

**A safety-focused Model Context Protocol (MCP) server for Microsoft Teams Phone
administration.**

TeamsPhone MCP gives MCP-compatible assistants a structured way to inspect, diagnose,
report on, and change Teams Phone configuration. Instead of exposing a general-purpose
shell, it provides a catalog of narrowly scoped tools with validated inputs, predictable
outputs, and safeguards around every write.

> [!WARNING]
> TeamsPhone MCP is experimental and not ready for production use. Test it in a
> non-production Microsoft 365 tenant. Interfaces, manifests, configuration, and audit
> formats may change before the first supported release.

## Why TeamsPhone MCP?

Teams Phone administration often requires moving between PowerShell cmdlets, Microsoft
Graph data, and several related configuration objects. That makes routine investigations
slow and multi-step changes easy to get wrong.

TeamsPhone MCP is designed to make that work:

- **Easier to explore:** ask an MCP client to retrieve voice configuration, inventory,
  routing, licensing, emergency-location, and usage data through purpose-built tools.
- **Faster to troubleshoot:** run diagnostics that turn raw tenant state into ordered,
  actionable findings.
- **Safer to automate:** preview changes before execution, bind approvals to exact
  parameters, verify results, and roll back higher-risk operations when possible.
- **Consistent to integrate:** every tool uses a strict manifest, schema-validated
  arguments, and a common result envelope.
- **Auditable by default:** write activity can be recorded locally with redaction,
  snapshots, retention controls, and report exports.

## Who is it for?

- **Teams Phone and Microsoft 365 administrators** evaluating assistant-driven
  operations.
- **Unified communications engineers** investigating voice configuration and call
  routing.
- **Managed service providers and platform teams** prototyping repeatable,
  tenant-scoped workflows.
- **MCP developers and security practitioners** interested in constrained,
  policy-governed administrative tooling.

This project assumes familiarity with Microsoft Teams Phone, Microsoft Entra app
registrations, and MCP clients. It is not a replacement for Microsoft administration
portals, change review, least-privilege access, or operational oversight.

## Features

### Understand tenant configuration

- Inspect users, phone numbers, call queues, auto attendants, resource accounts,
  schedules, emergency addresses, licenses, and voice policies.
- Capture a tenant-wide voice snapshot.
- Page through large tenant inventories with signed continuation tokens.

### Diagnose voice services

- Diagnose a user's licensing, Enterprise Voice, number, policy, dial-plan, and
  emergency-location state.
- Trace recursive auto-attendant and call-queue flows, including loops and broken
  references.
- Test dial-plan normalization and identify the matching rule.
- Check call-queue health, orphaned objects, and overall tenant health.
- Analyze PSTN usage and per-user call-quality evidence from Microsoft Graph call
  records.

### Report on operations

- Report on number and license utilization, emergency coverage, and policy assignments.
- Query tenant-scoped local change history.
- Export audit and operational reports as Markdown or CSV.

### Make guarded changes

- Assign, remove, and move phone numbers.
- Onboard and offboard voice users.
- Update queue membership, calling policies, voicemail, caller ID, and emergency
  location assignments.
- Run writes through a staged snapshot → preflight → dry-run → execute → verify →
  rollback pipeline.

### Operate with safety controls

- Dry-run is the default for every write.
- Execution requires a short-lived HMAC confirmation token bound to the tenant, tool,
  and exact business parameters.
- Risk tiers and blast-radius limits govern write behavior.
- Server-wide and per-session `whatif` or `readonly` ceilings can prevent execution.
- HTTP fails closed without a bearer token and applies per-session rate limits.
- There is no arbitrary command or script execution tool.
- Tenant credentials are certificate-based, tenant-bound, and supplied through local
  configuration—never built into the repository.

## How it works

```text
MCP client
    │  Streamable HTTP or stdio
    ▼
ASP.NET Core host
    ├── authentication, rate limiting, and correlation logging
    ├── manifest and raw-argument validation
    ├── risk policy and confirmation-token enforcement
    └── tenant-scoped session coordination
            │
            ├── MicrosoftTeams PowerShell
            ├── Microsoft Graph call records
            └── local JSONL audit store
```

Each tenant tool is defined by a strict `manifest.yaml` and a PowerShell implementation
under [`tools/`](./tools). The host validates manifest and exposed MCP contract parity at
startup, then validates raw call arguments again before invoking a tool.

## Tool catalog

TeamsPhone MCP currently exposes 38 tools. The main administrative tools are grouped
below; the catalog also includes `ping` and a mocked write used to demonstrate the
safety protocol.

<details>
<summary><strong>Read-only configuration tools (10)</strong></summary>

| Tool | Purpose |
| --- | --- |
| `get-user-voice-config` | Retrieve one user's voice configuration |
| `get-tenant-voice-snapshot` | Summarize tenant voice resources and policies |
| `list-phone-numbers` | List phone-number inventory and assignments |
| `get-callqueue-config` | Retrieve queue routing, agents, and fallback behavior |
| `get-autoattendant-config` | Retrieve call flows, menus, and targets |
| `check-user-licensing` | Check voice-relevant licenses and service plans |
| `list-emergency-addresses` | List emergency locations and validation state |
| `list-voice-policies` | List routing, dial-plan, calling, and voicemail policies |
| `list-resource-accounts` | List resource accounts and their associations |
| `get-schedules` | List schedules and referencing auto attendants |

</details>

<details>
<summary><strong>Diagnostics and reports (13)</strong></summary>

| Tool | Purpose |
| --- | --- |
| `diagnose-user-voice` | Produce ordered user voice findings and suggested fixes |
| `trace-call-flow` | Trace number, resource account, auto-attendant, and queue paths |
| `test-dialplan-number` | Test effective number normalization for a user |
| `diagnose-callqueue-health` | Find agent, presence, identity, and target issues |
| `get-pstn-usage` | Summarize PSTN calls, cost, and failures for up to 90 days |
| `get-call-quality-summary` | Analyze per-user Graph call records for up to 30 days |
| `find-orphaned-objects` | Find broken, incomplete, empty, or unused voice objects |
| `run-tenant-health-check` | Rank tenant-wide capacity, licensing, and routing findings |
| `report-number-utilization` | Summarize number assignment and availability |
| `report-license-utilization` | Summarize voice license usage and readiness |
| `report-emergency-coverage` | Report user emergency-location coverage |
| `report-policy-assignments` | Export a user-by-policy matrix |
| `report-change-history` | Render locally audited changes as Markdown or CSV |

</details>

<details>
<summary><strong>Local audit tools (3)</strong></summary>

| Tool | Purpose |
| --- | --- |
| `query-audit-log` | Search tenant-scoped local audit records |
| `get-change-detail` | Retrieve a record and its available snapshots |
| `export-audit-report` | Export records for a tenant and UTC period |

These tools read only the configured local audit store and do not open a tenant
PowerShell session. See [Audit trail](./docs/audit.md).

</details>

<details>
<summary><strong>Write tools (10)</strong></summary>

| Tool | Tier | Purpose |
| --- | ---: | --- |
| `assign-phone-number` | 1 | Enable voice and assign a number with optional policies and location |
| `remove-phone-number` | 2 | Release a user's number while preserving restorable metadata |
| `move-number-between-users` | 2 | Move a number between users with verification and rollback |
| `onboard-voice-user` | 2 | Compose number, policy, caller ID, voicemail, and location setup |
| `offboard-voice-user` | 2 | Remove queue membership and voice assignments |
| `update-callqueue-members` | 1 | Replace a queue's direct user membership |
| `update-user-calling-policies` | 1 | Assign existing routing, dial-plan, and calling policies |
| `update-user-voicemail-settings` | 1 | Update voicemail and out-of-office behavior |
| `set-caller-id-assignment` | 1 | Assign an existing caller ID policy |
| `update-user-emergency-location` | 2 | Assign a validated emergency location |

See [Write tools](./docs/write-tools.md) for the confirmation and rollback protocol.

</details>

## Getting started

### Prerequisites

The recommended path uses Docker Compose:

- Docker Engine or Docker Desktop with Compose v2
- A Microsoft Entra application with app-only certificate authentication
- An MCP client that supports Streamable HTTP

For local development, install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
and PowerShell. The container already includes the pinned .NET runtime, PowerShell, and
MicrosoftTeams module.

### 1. Prepare Microsoft 365 access

Follow [Set up an Entra app](./docs/setup-entra-app.md) to register an application,
grant the required Microsoft Graph permissions and Teams role, and create a
certificate. Use a non-production tenant and grant only the permissions needed by the
tools you intend to call.

### 2. Configure the server

```bash
cp .env.example .env
```

Fill in the tenant, client, and certificate values. Generate separate random values for
the HTTP bearer token and confirmation-token signing key:

```bash
openssl rand -base64 32
openssl rand -base64 32
```

The default Compose configuration binds to `127.0.0.1`, persists audit records in a
named volume, and starts in `whatif` mode.

### 3. Start the server

```bash
docker compose config --quiet
docker compose up --build --detach
```

Connect your MCP client to `http://127.0.0.1:5199/mcp` using Streamable HTTP and
configure it with the HTTP bearer credential from `.env`.

Calls to tenant tools use the configured tenant ID and `credentialRef: "default"`.
See the [container guide](./docs/container.md) for certificate mounts, verification,
audit persistence, updates, and shutdown instructions.

### Local stdio option

For local development or a trusted MCP client that launches its own process:

```bash
dotnet run --project src/TeamsPhoneMcp.Host -- --stdio
```

Stdio is treated as a locally trusted transport and does not use HTTP bearer
authentication. Tenant tools still require configured Entra credentials, and all write
policy controls still apply.

## Configuration

| Setting | Environment variable | Default |
| --- | --- | --- |
| HTTP bearer token | `TEAMSPHONE_MCP_BEARER_TOKEN` | Required for HTTP |
| Named client tokens | `Auth__ClientTokens__<clientId>` | One token per caller; several accepted at once |
| Confirmation signing key | `TEAMSPHONE_MCP_CONFIRMATION_TOKEN_KEY` | Ephemeral key if omitted |
| Server execution ceiling | `TEAMSPHONE_MCP_MODE` | `full` outside Compose; Compose uses `whatif` |
| HTTP bind address | `ASPNETCORE_URLS` | Host configuration |
| HTTP rate-limit permits | `RateLimiting__PermitLimit` | `30` |
| HTTP rate-limit window | `RateLimiting__Window` | `00:01:00` |
| Tenant session idle timeout | `TenantSessions__IdleTimeout` | `00:10:00` |
| Maximum tenant sessions | `TenantSessions__MaxSessions` | `10` |
| Manifest root override | `ToolManifests__ToolsRootPath` | Bundled `tools/` |
| Audit configuration | `Audit__Enabled`, `Audit__RootPath`, `Audit__RetentionDays` | See [audit guide](./docs/audit.md) |

The server mode can be `full`, `whatif`, or `readonly`. A mode is a ceiling: a client
session can request a more restrictive mode, but cannot elevate beyond the server
setting.

### Naming your clients

`TEAMSPHONE_MCP_BEARER_TOKEN` is recorded in the audit trail as the client `default`.
To tell callers apart — and to rotate a token without downtime — give each one its own
entry instead:

```bash
export Auth__ClientTokens__orchestrator='…'
export Auth__ClientTokens__inspector='…'
```

Every configured token is accepted simultaneously, so rotation is: add the new entry,
move the caller across, then remove the old one. The `clientId` written to the audit
trail is always the one the server matched, never a value the client asserts, so
attribution cannot be forged. Startup fails if two clients share a token, because the
audit trail could not then tell them apart. A session belongs to the client that opened
it: presenting another client's `Mcp-Session-Id` is rejected with `401`.

## Project status and roadmap

Milestones M1–M5.5 are complete. M6 hardening is in progress, including documentation
polish and preparation for the first supported release.

Implemented today:

- Streamable HTTP and stdio transports
- Certificate-authenticated Microsoft 365 tenant sessions
- Read, diagnostic, reporting, local audit, and user-level write tools
- Container packaging and automated .NET, Pester, and secret-scanning checks

Planned:

- Shared-object write operations
- A supported tagged release artifact
- Post-v1 hash-chained audit records and OpenTelemetry export

The detailed milestone history and acceptance criteria are in
[`teamsphone-mcp-build-spec`](./teamsphone-mcp-build-spec).

## Development

```bash
dotnet build TeamsPhoneMcp.sln
dotnet test TeamsPhoneMcp.sln
pwsh -NoProfile -c "Invoke-Pester -Path tools"
```

The solution targets .NET 8 and treats compiler warnings as errors. See
[Testing](./docs/testing.md) for the full automated, smoke, and gated live-tenant test
playbook.

### Repository layout

| Path | Purpose |
| --- | --- |
| `src/TeamsPhoneMcp.Host/` | ASP.NET Core host, transports, auth, logging, and rate limiting |
| `src/TeamsPhoneMcp.Core/` | Tool registration, manifests, policy, execution, and sessions |
| `src/TeamsPhoneMcp.Credentials/` | Local certificate credential resolution |
| `src/TeamsPhoneMcp.Audit/` | Redacted JSONL records, snapshots, queries, and retention |
| `tools/` | Tool manifests, PowerShell implementations, and Pester tests |
| `tests/unit/` | xUnit unit, integration, and host acceptance tests |
| `docs/` | Setup, operations, audit, write-tool, and testing guides |

## Contributing

Contributions, bug reports, and focused feature proposals are welcome. Please read
[`CONTRIBUTING.md`](./CONTRIBUTING.md) before opening a pull request, and open an issue
before beginning a broad architectural change.

Use synthetic data only. Do not include tenant names, customer identifiers, real phone
numbers, certificates, or credentials in issues, tests, examples, or pull requests.

## Security

Do not report vulnerabilities in a public issue. Follow [`SECURITY.md`](./SECURITY.md)
to submit a private security advisory.

This is pre-release software with no currently supported production version or security
service-level commitment.

## License

Licensed under the [Apache License 2.0](./LICENSE).
