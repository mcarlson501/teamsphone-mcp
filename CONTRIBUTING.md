# Contributing to teamsphone-mcp

Thanks for your interest in contributing. This is a pre-release project under active
development. Public APIs and manifest contracts may change before the first release,
and the repository is not ready for production or live-tenant use.

The current surface includes the M1 manifest/tool/policy boundary and the offline M2
tenant-session foundation. PowerShell, credentials, and live Microsoft 365 integration
remain intentionally unavailable. Open an issue before starting a broad architectural
change so the work can be aligned with the milestone plan.

## Ground rules (from the build spec)

- **Deterministic execution.** The model chooses tools and parameters; server code
  does the work. There is **no** generic "run anything" tool, and none will be added.
- **Security is acceptance-blocking.** Client-facing auth exists from day one. Never
  commit secrets, tenant names, or real phone numbers in code, tests, or fixtures.
- **Use synthetic data only.** Tests, examples, issue reports, and pull requests must
  not include customer identifiers or data copied from a live tenant.
- **Small PRs.** Each milestone has acceptance criteria a human verifies before the
  next begins. Keep changes focused.

## Development workflow

```bash
dotnet build TeamsPhoneMcp.sln
dotnet test  TeamsPhoneMcp.sln
```

For the full testing playbook — PowerShell (Pester) tests, a local server smoke test, and a
gated live end-to-end call against a real tenant — see [docs/testing.md](docs/testing.md).

- Target framework: **.NET 8** (pinned via `global.json`).
- Shared build settings live in `Directory.Build.props` (`nullable`, implicit usings,
  warnings-as-errors). Keep the build warning-clean.

## Project structure

| Path                        | Purpose                                                        |
| --------------------------- | ------------------------------------------------------------- |
| `src/TeamsPhoneMcp.Host/`   | Entrypoint, transports, auth + correlation-logging middleware. |
| `src/TeamsPhoneMcp.Core/`   | Tools, policy, manifests, and tenant-session lifecycle.        |
| `tests/unit/`               | xUnit tests.                                                   |

## Adding a tool

1. Copy `tools/_template/` to `tools/<tool-id>/` and set a kebab-case `id` that
  exactly matches the folder name.
2. Define every input explicitly. Supported M1 types are `string`, `integer`,
  `number`, and `boolean`; the supported format is `upn`.
3. Add the C# handler under `src/TeamsPhoneMcp.Core/Tools/` and register it in
  `AddTeamsPhoneTools`.
4. Keep the manifest inputs, required fields, and annotations exactly aligned with
  the generated MCP contract.
5. Add catalog, validation, policy, and host acceptance coverage appropriate to the
  tool's risk tier.

The host rejects unknown manifest fields and fails startup for missing, orphaned, or
mismatched tool contracts. At invocation, raw arguments are validated before C#
binding. Do not add a manifest without a handler or expose a handler without a
manifest.

## Definition of done (every PR)

- Manifest and C# schema/annotations remain in parity.
- Tests included and green, including malformed and rejection paths.
- No secrets, tenant names, or real numbers anywhere.
- Docs updated when contracts or setup change.
