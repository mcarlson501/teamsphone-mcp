# Contributing to teamsphone-mcp

Thanks for your interest in contributing! This document is intentionally brief for
the M0 skeleton and will grow as the tool contract and PowerShell surface land in
later milestones.

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

## Adding a tool (today)

For M0, tools are plain C# methods annotated with `[McpServerTool]` inside a
`[McpServerToolType]` class under `src/TeamsPhoneMcp.Core/Tools/`, then registered in
`ToolRegistration.AddTeamsPhoneTools`. See `Tools/PingTool.cs` as the template.

> A manifest-driven registry (`tools/<name>/manifest.yaml` + `run.ps1` + tests) is
> introduced in milestone M1; this section will be replaced then.

## Definition of done (every PR)

- Tests included and green.
- No secrets, tenant names, or real numbers anywhere.
- Docs updated when contracts or setup change.
