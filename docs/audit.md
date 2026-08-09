# Audit trail

Every tool call the server accepts produces exactly one audit record — successes,
policy rejections, validation failures, and tenant errors alike. Records are written
as newline-delimited JSON (JSONL) to the local filesystem, partitioned by tenant and
by UTC day.

---

## Configuration

| Setting | Config key / env var | Default | Purpose |
| ------- | -------------------- | ------- | ------- |
| Enabled | `Audit:Enabled` / `Audit__Enabled` | `true` | Turn the JSONL sink and retention sweeper on or off |
| Root path | `Audit:RootPath` / `Audit__RootPath` | `audit` | Where records are written. Relative paths resolve against the host content root; absolute paths are used as supplied |
| Retention | `Audit:RetentionDays` / `Audit__RetentionDays` | `400` | Days to keep daily files and snapshot folders |
| Sweep interval | `Audit:SweepIntervalHours` / `Audit__SweepIntervalHours` | `24` | How often the retention sweeper runs (it also runs once at startup) |

```jsonc
{
  "Audit": {
    "Enabled": true,
    "RootPath": "audit",
    "RetentionDays": 400,
    "SweepIntervalHours": 24
  }
}
```

Setting `Enabled: false` swaps in a no-op sink: the server still runs, but nothing is
persisted. Audit failures never fail a tenant operation — write errors are logged and
swallowed.

---

## On-disk layout

```
{RootPath}/
  {tenantId}/
    2026-07-31.jsonl                                   one JSON record per line
    snapshots/
      2026-07-31/
        {correlationId}-before.json
        {correlationId}-after.json
  _system/
    2026-07-31.jsonl                                   retention-sweep records
```

Tenant and date segments are sanitized before use, so a hostile `tenantId` cannot
escape the audit root.

---

## Record shape

```jsonc
{
  "recordVersion": 1,
  "timestamp": "2026-07-31T20:25:15.627314+00:00",
  "correlationId": "ae275f3c-362d-4470-bb00-9787d34f28b5",
  "sessionId": "…",              // MCP session id, when the transport supplies one
  "clientId": "orchestrator",    // server-derived from the matched bearer token
  "reportedClient": "inspector/0.1.0", // unverified name/version the client claims
  "tenantId": "11111111-…",
  "toolId": "mock-write-user-policy",
  "toolVersion": "1.0.0",
  "status": "Failed",            // Succeeded | DryRunCompleted | Failed
  "errorCode": "missingConfirmationToken",
  "errorMessage": "A valid confirmationToken is required.",
  "dryRun": false,
  "simulated": false,
  "riskTier": 2,
  "parameters": { "targetUserUpn": "user@contoso.com", "policyName": "US-Calling" },
  "stages": [ { "stage": "execute", "durationMs": 42 } ],
  "checks": [ { "phase": "preflight", "check": "userExists", "passed": true } ],
  "durationMs": 42,
  "snapshotRefs": {
    "before": "11111111-…/snapshots/2026-07-31/ae275f3c-…-before.json",
    "after":  "11111111-…/snapshots/2026-07-31/ae275f3c-…-after.json"
  }
}
```

Null fields are omitted. `correlationId` is lifted from the tool's own result envelope
when the tool produced one, so a client-facing error and its audit record always share
the same id.

### Who did it: `clientId` vs `reportedClient`

`clientId` is the name of the configured `Auth:ClientTokens` entry whose token the host
matched on the request (or `default` for the single-token form). The client cannot
influence it, so attribution stands up to scrutiny. `reportedClient` is the
`clientInfo` name/version the client sent about itself: useful context, but unverified,
and it must not be relied on for attribution. On the stdio transport, which is treated
as locally trusted and carries no bearer token, `clientId` is absent.

---

## Redaction

Parameters are redacted **before** they are written, in three independent passes:

1. **Manifest opt-in** — any parameter named in a manifest's `redactParams` list.
2. **Name heuristics** — property names containing `secret`, `password`, `passphrase`,
   `privatekey`, `thumbprint`, `apikey`, `accesstoken`, `refreshtoken`, `bearertoken`,
   `certificatedata`, or `pfx`.
3. **Value heuristics** — PEM blocks, 40-character hex thumbprints, and JWT-shaped
   strings, wherever they appear (including inside error messages).

Redacted values are replaced with `***redacted***`. Reserved transport arguments
(`credentialRef`, `confirmationToken`, `pageSize`, `continuationToken`, …) are stripped
from `parameters` entirely — only business arguments are recorded.

---

## Retention

A background sweeper runs at startup and then every `SweepIntervalHours`. It deletes
daily files and snapshot folders whose **encoded date** is older than `RetentionDays`
(file modification times are ignored, so re-copied archives are not accidentally kept).
When anything is pruned, the sweeper writes a self-describing record under the `_system`
tenant with `toolId: "audit-retention-sweep"`.

---

## Querying through MCP

Three tier-0 tools expose the configured local audit root without opening a tenant
PowerShell session:

| Tool | Purpose |
| ---- | ------- |
| `query-audit-log` | Filter by tenant, UTC range, tool, status, or client; returns newest-first records with signed pagination |
| `get-change-detail` | Retrieve one tenant-scoped record and its available before/after snapshots by correlation id |
| `export-audit-report` | Render all records in an inclusive UTC range as Markdown or CSV |

`report-change-history` is a report-oriented alias of `export-audit-report`. Both use
the same tenant-bound query and renderer. Exported reports omit raw error messages;
they include the stable error code instead.

Continuation tokens are HMAC-signed and bound to the canonical query filters. A token
cannot be reused with another tenant or changed filter set. Snapshot paths are resolved
under the requested tenant directory, so a stored path cannot escape into another
tenant's records.

---

## Verifying the audit trail

Automated coverage lives in:

- `tests/unit/AuditRedactorTests.cs` — redaction rules
- `tests/unit/JsonlAuditSinkTests.cs` — file layout, append semantics, concurrency
- `tests/unit/AuditRetentionSweeperTests.cs` — expiry and the sweep record
- `tests/unit/ToolAuditRecorderTests.cs` — record construction and snapshot refs
- `tests/unit/AuditPipelineAcceptanceTests.cs` — end-to-end through a live MCP host,
  including a forced failure and a secret-shaped input that must never reach disk

```bash
dotnet test tests/unit --filter FullyQualifiedName~Audit
```

To inspect a real run, start the host, call a tool, then:

```bash
find audit -name '*.jsonl' -exec cat {} \; | tail -1 | python3 -m json.tool
```
