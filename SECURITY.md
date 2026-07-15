# Security Policy

## Reporting a vulnerability

Please report suspected vulnerabilities privately using GitHub's
[private security advisory](https://docs.github.com/en/code-security/security-advisories/guidance-on-reporting-and-writing-information-about-vulnerabilities/privately-reporting-a-security-vulnerability)
feature for this repository. Do **not** open a public issue for security reports.

We aim to acknowledge reports within a few business days.

## Development status

This project is pre-release, under active development, and not approved for
production or live-tenant use. There are no production-supported versions or security
service-level commitments. The current M1 implementation does not connect to
Microsoft Teams, Microsoft Graph, or Microsoft 365 and does not accept tenant
credentials.

The controls below describe the implemented host and policy boundary. Credential
handling, PowerShell execution, tenant sessions, and tenant isolation are M2 work and
must pass their milestone security tests before any live integration is considered.

## Security model (summary)

This project is intended to administer Microsoft Teams Phone against customer M365
tenants, so security is treated as acceptance-blocking, not optional:

- **Client-facing auth from day one.** The HTTP transport requires a static bearer
  token supplied via configuration/environment (`TEAMSPHONE_MCP_BEARER_TOKEN`).
  Unauthenticated requests to `/mcp` receive `401` with no tool listing. If no token
  is configured, the transport fails closed.
- **No secrets in the repo or logs.** Tokens and credentials are read from
  configuration/environment only; they are never hardcoded and never logged. Do not
  commit secrets, tenant names, or real phone numbers in code, tests, or fixtures.
- **No generic execution tool.** Every write is an enumerated, single-purpose,
  schema-validated tool. Raw arguments are checked before handler binding, and the
  host fails startup if its strict manifest and exposed tool contracts drift. There
  is no "run arbitrary script/command" capability.
- **Writes require two steps.** Write tools default to dry-run. Execution requires a
  short-lived HMAC confirmation token bound to the tool, tenant, and canonical
  business parameters; changed parameters, expired tokens, and cross-context token
  use are rejected.
- **Tenant isolation is not implemented yet.** M2 must make tenant identity immutable
  on each session and prove through isolation tests that no execution context crosses
  tenants. Until that boundary exists, this project must not connect to live tenants.

## Supported versions

No production version is currently supported. Security fixes for the development
codebase are made on the latest `main` branch.
