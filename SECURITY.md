# Security Policy

## Reporting a vulnerability

Please report suspected vulnerabilities privately using GitHub's
[private vulnerability report](https://github.com/mcarlson501/teamsphone-mcp/security/advisories/new)
feature for this repository. Do **not** open a public issue for security reports.

We aim to acknowledge reports within a few business days.

## Development status

This project is pre-release, under active development, and not approved for
production or live-tenant use. There are no production-supported versions or security
service-level commitments. The current implementation can connect to Microsoft Teams,
Microsoft Graph, and Microsoft 365 when an operator explicitly configures certificate
credentials. Use only a dedicated non-production tenant while the project remains
experimental.

## Security model (summary)

This project is intended to administer Microsoft Teams Phone against customer M365
tenants, so security is treated as acceptance-blocking, not optional:

- **Client-facing auth from day one.** The HTTP transport requires a bearer token
  supplied via configuration/environment (`TEAMSPHONE_MCP_BEARER_TOKEN`, or one
  `Auth:ClientTokens:<clientId>` entry per caller). Unauthenticated requests to `/mcp`
  receive `401` with no tool listing. If no token is configured, the transport fails
  closed. The `clientId` in the audit trail is derived server-side from the token that
  matched, never asserted by the client, and a session may only be used by the client
  that opened it.
- **No secrets in the repo or logs.** Tokens and credentials are read from
  configuration/environment only; they are never hardcoded and never logged. Do not
  commit secrets, tenant names, or real phone numbers in code, tests, or fixtures.
- **No generic execution tool.** Every write is an enumerated, single-purpose,
  schema-validated tool. Raw arguments are checked before handler binding, and the
  host fails startup if its strict manifest and exposed tool contracts drift. There
  is no "run arbitrary script/command" capability.
- **Writes require two steps.** Write tools default to dry-run. Execution requires a
  short-lived HMAC confirmation token bound to the tool, tenant, canonical business
  parameters, and the session and client that requested the dry-run. Changed
  parameters, expired tokens, cross-context use, and re-use of an already-redeemed
  token are all rejected. A token is **single use**: it carries a random `jti` that is
  recorded on redemption, so a write that fails after redemption needs a fresh
  dry-run rather than a retry with the old token.
- **Tenant sessions are identity-bound.** The session manager binds each session to an
  immutable tenant and credential reference, coordinates reads and writes, and has
  interleaved isolation tests. Certificate credentials and PowerShell runspaces are
  available for explicit non-production testing.
- **Contributed PowerShell is not sandboxed yet.** Until the S3 constrained-execution
  milestone lands, a merged `run.ps1` executes in-process with FullLanguage access and
  must be reviewed as host-trusted code. The project does not yet accept untrusted
  third-party tool implementations.

## Supported versions

No production version is currently supported. Security fixes for the development
codebase are made on the latest `main` branch.

## Confirmation-token signing key

Confirmation tokens are HMAC-SHA256 signatures produced with a single server-side key,
set via `TEAMSPHONE_MCP_CONFIRMATION_TOKEN_KEY` (or `Policy:ConfirmationTokenKey`) as
Base64 of at least 32 bytes. Generate one with
`ConfirmationTokenService.CreateRandomBase64Key()` or any CSPRNG.

**If the key is compromised, an attacker who can also authenticate to the HTTP
transport can forge confirmation tokens and skip the dry-run step entirely.** The key
is therefore as sensitive as the bearer tokens themselves. It is never logged and never
written to the audit trail.

Operating rules:

- **Do not share the key across instances unintentionally.** Two hosts sharing a key
  will honour each other's confirmation tokens. That is correct for replicas of one
  logical server and wrong for separate deployments — for example a staging host would
  be able to mint tokens a production host accepts. Give unrelated deployments
  different keys.
- **Replay protection is per process.** The redeemed-`jti` cache is in memory, so
  replicas sharing a key do not share the record of what has been spent. Until that
  cache is shared, run one host per signing key if single-use enforcement matters to
  you.
- **When no key is configured** the host generates an ephemeral one per process and
  logs a warning. Tokens then stop working across restarts, which is safe but confusing
  in production.
- **Rotation** invalidates every outstanding confirmation token immediately: in-flight
  dry-runs must be repeated. There is no dual-key acceptance window, deliberately — a
  token minted under a retired key should not be spendable. Rotate during a quiet
  period, or accept that pending confirmations need a new dry-run.
- **Rotate on suspicion of compromise, on operator offboarding, and whenever the host
  is redeployed from a snapshot that may have carried the key.**
