# Write tools (MACD)

Write tools change tenant state. The host enforces the write-safety protocol from build
spec §6.4 for every tool with `riskTier >= 1`; a tool script cannot opt out of it.

`move-number-between-users` remains the smallest full-depth reference implementation.
The Phase D catalog also includes atomic assignment/settings tools and two composites
that compensate completed child steps in reverse order.

## Phase D catalog

| Tool | Tier | Scope |
| ---- | ---- | ----- |
| `assign-phone-number` | 1 | Number, enterprise voice, optional policies and emergency location |
| `remove-phone-number` | 2 | Number release with rollback metadata |
| `move-number-between-users` | 2 | Reversible user-to-user number move |
| `onboard-voice-user` | 2 | Composite onboarding with reverse compensation |
| `offboard-voice-user` | 2 | Composite offboarding with a disposition report |
| `update-callqueue-members` | 1 | Direct queue agents |
| `update-user-calling-policies` | 1 | Existing user policy assignments |
| `update-user-voicemail-settings` | 1 | Per-user voicemail settings |
| `set-caller-id-assignment` | 1 | Existing caller ID policy assignment |
| `update-user-emergency-location` | 2 | Existing validated emergency location |

## The two-step protocol

1. **Dry run (default).** A call without `dryRun: false` never writes. The host runs
   `snapshot → preflight → dryrun` and returns `status: DryRunCompleted` with a
   `confirmationToken` (HMAC over a random `jti` + toolId + tenantId + canonicalized
   params + the issuing session and client + expiry, 15 minute TTL) plus the planned
   changes in `diff.after`.
2. **Execute.** Calling again with `dryRun: false` **and** that token runs
   `snapshot → execute → verify`. Any change to the parameters invalidates the token, so
   a changed plan always requires a fresh dry run.

A token is **single use** and **bound to its caller**. Redeeming it records its `jti`,
so the same token cannot be presented twice, and it is only redeemable in the session
and by the client that requested the dry-run. One consequence worth knowing: a write
that fails *after* the token was accepted needs a fresh dry-run rather than a retry
with the same token.

| Error code | Meaning |
| --- | --- |
| `missingConfirmationToken` | Execute was requested without a token. |
| `invalidConfirmationToken` | Signature, tool, tenant, or parameters do not match. |
| `expiredConfirmationToken` | Past the 15 minute TTL. |
| `replayedConfirmationToken` | Already redeemed; repeat the dry-run. |
| `sessionBoundConfirmationToken` | Issued to a different MCP session. |
| `clientBoundConfirmationToken` | Issued to a different authenticated client. |
| `confirmationTokenCacheExhausted` | The host cannot currently guarantee single use, so it refuses rather than risk a replay. |

`whatIf` is an accepted alias for `dryRun`. A session initialized with
`_meta.whatIfMode: true`, or a server started with `TEAMSPHONE_MCP_MODE=whatif`, forces
every write call through the dry-run path even when it requests `dryRun: false`. The
result has `simulated: true` and no confirmation token. `readonly` hides tier 1+ tools
entirely.

Example (arguments only — transport details are in the README):

```jsonc
// 1. dry run
{ "tenantId": "…", "credentialRef": "contoso",
  "sourceUserUpn": "alice@contoso.com", "targetUserUpn": "bob@contoso.com" }

// 2. execute, using the token returned by step 1
{ "tenantId": "…", "credentialRef": "contoso",
  "sourceUserUpn": "alice@contoso.com", "targetUserUpn": "bob@contoso.com",
  "dryRun": false, "confirmationToken": "…" }
```

## Stage pipeline

| Stage | Runs on | Purpose |
| ----- | ------- | ------- |
| `snapshot` | dry run + execute | Capture pre-change state. Its output *is* the envelope's `diff.before` and is threaded back into every later stage as `.snapshot`. |
| `preflight` | dry run | Validate preconditions against live tenant data. Returns `checks`. |
| `dryrun` | dry run | Render the planned cmdlet calls and the projected state. Writes nothing. |
| `execute` | execute | Apply the change. Must be idempotent. |
| `verify` | execute | Prove the change landed. Returns `checks`. |
| `rollback` | execute, on failure | Undo the change using the snapshot. Required for `riskTier >= 2`. |

Stage results are single JSON objects emitted with the `tools/common` helpers:
`Write-StageSnapshot -State` for the snapshot stage, `Write-StageResult -Summary -After
-Checks` for everything else.

### Checks are authoritative

A `preflight` or `verify` stage reports outcomes structurally rather than by throwing, so
the failing check survives into the envelope and the audit record:

- any preflight check with `passed: false` → `status: preflightFailed`, **no token
  issued**, nothing written;
- any verification check with `passed: false` → rollback runs → `status:
  verifyFailedRolledBack`, with both `diff.before` and `diff.after` retained.

Throwing (or writing to stderr) still fails a stage; use it for unexpected conditions,
not for a precondition you can describe as a check.

### Preflight has to encode what the tenant will actually accept

Teams rejects many ineligible writes with an opaque `BadRequest`, which reaches the
caller as a generic `executionFailed` after a rollback. Every rejection you can predict
from the snapshot belongs in preflight instead. Two that the live run surfaced for
`move-number-between-users`:

- **account type** — an account carrying resource-account licences (`VoiceApp`,
  `TeamsRoom*`) reports `AccountType: ResourceAccount` and cannot hold a *user* number,
  even though it looks voice-capable;
- **licence matched to the number type** — a `CallingPlan` number needs a Calling Plan
  licence on the target. `PhoneSystem` alone is not enough.
- **emergency location** — Calling Plan assignment requires a validated LIS location.
  Current Teams objects report this as `ValidationStatus: Validated`; older module
  objects may expose `IsValidated`.

When the tenant reports nothing for a property, prefer passing the check with an
explanatory `detail` over failing — the write itself still fails closed.

### Drift protection

The execute stage re-reads live state and refuses to write when the tenant no longer
matches the snapshot — the confirmation token proves *intent*, not *freshness*. Rollback
is written to be safe to run when nothing changed.

## Authoring checklist

- `manifest.yaml`: `category` (`move|add|change|delete`), `riskTier`, `maxBlastRadius`,
  `annotations`, the reserved inputs the tool accepts (`dryRun`, `whatIf`,
  `confirmationToken`, `blastRadius`), and the human-readable `preflight`,
  `verification`, and `rollback` documentation fields.
- `run.ps1`: implement all six stages; use `Invoke-WithRetry` for tenant calls and
  `Wait-ForCondition` for replication lag — never a bare `Start-Sleep` loop.
- `run.Tests.ps1`: cover every stage offline with mocked cmdlets, including the
  idempotent re-run, the drift refusal, a failing verification, and rollback.
- Add nothing to the host engine.

## Audit

Every call writes exactly one audit record. For write tools the record carries
`snapshotRefs.before` / `snapshotRefs.after`, pointing at JSON files under
`{auditRoot}/{tenantId}/snapshots/`, so a rolled-back change is fully reconstructable.
See [`audit.md`](./audit.md).

## Testing

```bash
dotnet test                                              # engine + acceptance suites
pwsh -NoProfile -c "Invoke-Pester -Path tools -Output Detailed"
```

`tests/unit/WritePipelineAcceptanceTests.cs` proves the milestone criteria offline:
dry-run → confirm → execute → verify, a forced verification failure producing
`verifyFailedRolledBack` with both snapshots stored, a blocked preflight that issues no
token, and token invalidation on changed parameters.

### Live tenant verification

A live run of a write tool **changes the tenant**. Use a dev tenant, a spare number, and
two licensed test users — never a production tenant. Do the dry run first and read the
`plannedCommands` in `diff.after` before supplying the confirmation token. The audit
trail under the configured `Audit:RootPath` is the record of what happened; the snapshot
files are what you restore from if you need to undo the move manually.

`tests/unit/MoveNumberIntegrationTests.cs` automates both halves against a real tenant —
the full move (and the move back, so the tenant is left as found) and a preflight-blocked
ineligible target that issues no token. See [`testing.md`](./testing.md) Layer 4.

`tests/unit/PhaseDIntegrationTests.cs` signs off both composites by offboarding one
explicitly isolated numbered user and onboarding that user from the captured snapshot
in a `finally` block. It verifies the original number, emergency location, enterprise
voice state, and policies are restored. See [`testing.md`](./testing.md) Layer 4.
