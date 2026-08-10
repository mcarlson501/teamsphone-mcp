# Contributing to teamsphone-mcp

Thanks for your interest in contributing. This is an experimental `0.x` project under
active development. Public APIs and manifest contracts may change between minor
releases, and the repository is not ready for production or live-tenant use.

The current surface includes manifest-driven read and write tools, Streamable HTTP and
stdio transports, certificate-authenticated tenant sessions, audit recording, guided
setup, and container packaging. Milestones M1-M6.5 are implemented for the `v0.1.0`
release candidate; external Ubuntu, VS Code, and demo-tenant acceptance remains before
the tag. Report bugs and focused enhancement proposals through
[GitHub Issues](https://github.com/mcarlson501/teamsphone-mcp/issues); open an issue
before starting a broad architectural change so it can be aligned with the milestone
plan.

## Ground rules (from the build spec)

- **Deterministic execution.** The model chooses tools and parameters; server code
  does the work. There is **no** generic "run anything" tool, and none will be added.
- **Security is acceptance-blocking.** Client-facing auth exists from day one. Never
  commit secrets, tenant names, or real phone numbers in code, tests, or fixtures.
- **Use synthetic data only.** Tests, examples, issue reports, and pull requests must
  not include customer identifiers or data copied from a live tenant.
- **Treat contributed PowerShell as host-trusted code.** Until the S3 constrained
  execution milestone lands, `run.ps1` executes in-process with FullLanguage access.
  Third-party tool contributions are not accepted without explicit maintainer review
  of that risk.
- **Small PRs.** Each milestone has acceptance criteria a human verifies before the
  next begins. Keep changes focused.

## Development workflow

```bash
dotnet build TeamsPhoneMcp.sln
dotnet test  TeamsPhoneMcp.sln
pwsh -NoProfile -File scripts/lint-tools.ps1
```

For the full testing playbook — PowerShell (Pester) tests, a local server smoke test, and a
gated live end-to-end call against a real tenant — see [docs/testing.md](docs/testing.md).

Every behavior change must add or update automated tests. Major functionality requires
happy-path coverage plus malformed-input and rejection-path coverage appropriate to its
risk; a change is not complete until those tests pass in CI.

- Target framework: **.NET 8** (pinned via `global.json`).
- Shared build settings live in `Directory.Build.props` (`nullable`, implicit usings,
  warnings-as-errors). Keep the build warning-clean.
- Dependencies are locked. `packages.lock.json` is committed per project and CI restores
  with `--locked-mode`; run `dotnet restore` and commit the updated lock files whenever
  you change a `PackageReference`.
- NuGet auditing is on for transitive packages at `low` severity, so a new advisory fails
  the build. Pin the patched version explicitly rather than suppressing the warning.
- `Invoke-Expression`/`iex`, `Add-Type`, `Start-Process`/`saps`/`start`, and
  `[ScriptBlock]::Create` are rejected by the PSScriptAnalyzer gate anywhere under
  `tools/` and `scripts/`. These are not style preferences — they defeat the policy
  engine, and there is no approved use of them in this repository.

## Project structure

| Path                        | Purpose                                                        |
| --------------------------- | ------------------------------------------------------------- |
| `src/TeamsPhoneMcp.Host/`   | Entrypoint, transports, auth + correlation-logging middleware. |
| `src/TeamsPhoneMcp.Core/`   | Tools, policy, manifests, and tenant-session lifecycle.        |
| `tests/unit/`               | xUnit tests.                                                   |

## Adding a tool

1. Copy `tools/_template/` to `tools/<tool-id>/` and set a kebab-case `id` that
  exactly matches the folder name.
2. Define every input explicitly. Supported types are `string`, `integer`,
  `number`, and `boolean`; the supported format is `upn`.
3. Implement the stages your tool needs in `run.ps1` (tier-0 reads implement only
  `execute`) and add a `run.Tests.ps1` Pester suite alongside it. A manifest plus a
  `run.ps1` auto-registers as a pipeline tool — **do not edit the host engine**.
4. Keep the manifest inputs, required fields, and annotations exactly aligned with
  the generated MCP contract.
5. Add catalog, validation, policy, and host acceptance coverage appropriate to the
  tool's risk tier.

Write tools (`riskTier >= 1`) additionally implement the dry-run/confirmation-token
protocol, verification, and — from tier 2 — rollback. Read
[docs/write-tools.md](docs/write-tools.md) and use
`tools/move-number-between-users/` as the reference implementation.

The host rejects unknown manifest fields and fails startup for missing, orphaned, or
mismatched tool contracts. At invocation, raw arguments are validated before C#
binding. Do not add a manifest without a handler or expose a handler without a
manifest.

## Definition of done (every PR)

- Manifest and C# schema/annotations remain in parity.
- Tests included and green, including malformed and rejection paths.
- No secrets, tenant names, or real numbers anywhere.
- Docs updated when contracts or setup change.
