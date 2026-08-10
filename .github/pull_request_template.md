## Summary

<!-- What problem does this PR solve, and why is this the smallest appropriate change? -->

## Related issue

<!-- Link the issue or explain why one is not needed. -->

## Validation

<!-- List exact commands and relevant manual/live acceptance evidence. -->

## Checklist

- [ ] The change is focused and follows existing repository patterns.
- [ ] Tests cover happy, malformed, rejection, and failure paths appropriate to the risk.
- [ ] `dotnet format TeamsPhoneMcp.sln --verify-no-changes --no-restore` passes.
- [ ] Release build and relevant .NET/Pester/analyzer tests pass.
- [ ] Manifests, generated MCP contracts, annotations, and tool versions remain aligned.
- [ ] Write behavior preserves dry-run, confirmation, verification, blast-radius, and rollback rules.
- [ ] Audit records are produced for all affected success and failure paths.
- [ ] Documentation and changelog entries are updated when behavior or setup changes.
- [ ] Only synthetic data is present; no tenant/customer identifiers, real phone numbers, credentials, certificates, or private audit evidence are included.
- [ ] New dependencies and GitHub Actions are pinned and pass vulnerability/secret scanning.

## Live tenant impact

<!-- State "not run/not applicable" or describe sanitized fixture, baseline, result, and restoration evidence. -->