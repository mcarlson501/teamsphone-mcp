# Contributing to teamsphone-mcp

Thanks for your interest in contributing. The current surface is the M1 manifest,
C# tool, and policy boundary; PowerShell execution lands in M2.

## Ground rules (from the build spec)

- **Deterministic execution.** The model chooses tools and parameters; server code
  does the work. There is **no** generic "run anything" tool, and none will be added.
- **Security is acceptance-blocking.** Client-facing auth exists from day one. Never
  commit secrets, tenant names, or real phone numbers in code, tests, or fixtures.
- **Small PRs.** Each milestone has acceptance criteria a human verifies before the
  next begins. Keep changes focused.

## Development workflow

```bash
dotnet build TeamsPhoneMcp.sln
dotnet test  TeamsPhoneMcp.sln
```

- Target framework: **.NET 8** (pinned via `global.json`).
- Shared build settings live in `Directory.Build.props` (`nullable`, implicit usings,
  warnings-as-errors). Keep the build warning-clean.

## Project structure

| Path                        | Purpose                                                        |
| --------------------------- | ------------------------------------------------------------- |
| `src/TeamsPhoneMcp.Host/`   | Entrypoint, transports, auth + correlation-logging middleware. |
| `src/TeamsPhoneMcp.Core/`   | Tools and the central `AddTeamsPhoneTools` registration seam.  |
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
