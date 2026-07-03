# Security Policy

## Reporting a vulnerability

Please report suspected vulnerabilities privately using GitHub's
[private security advisory](https://docs.github.com/en/code-security/security-advisories/guidance-on-reporting-and-writing-information-about-vulnerabilities/privately-reporting-a-security-vulnerability)
feature for this repository. Do **not** open a public issue for security reports.

We aim to acknowledge reports within a few business days.

## Security model (summary)

This server administers Microsoft Teams Phone against customer M365 tenants, so
security is treated as acceptance-blocking, not optional:

- **Client-facing auth from day one.** The HTTP transport requires a static bearer
  token supplied via configuration/environment (`TEAMSPHONE_MCP_BEARER_TOKEN`).
  Unauthenticated requests to `/mcp` receive `401` with no tool listing. If no token
  is configured, the transport fails closed.
- **No secrets in the repo or logs.** Tokens and credentials are read from
  configuration/environment only; they are never hardcoded and never logged. Do not
  commit secrets, tenant names, or real phone numbers in code, tests, or fixtures.
- **No generic execution tool.** Every write is an enumerated, single-purpose,
  schema-validated tool. There is no "run arbitrary script/command" capability.
- **Tenant isolation.** Execution context is never reused across tenants (enforced in
  later milestones as the PowerShell session layer lands).

## Supported versions

The project is pre-1.0 and under active milestone development; only the latest
`main` is supported.
