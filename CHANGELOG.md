# Changelog

This file records notable changes and version-specific operational constraints.

## [0.1.0] - Unreleased

The first tagged version is an experimental release for evaluation against a dedicated
non-production Microsoft 365 tenant. It includes 38 manifest-validated MCP tools,
certificate-authenticated tenant sessions, guarded two-step writes, local audit records,
and container packaging.

### Distribution

- The GitHub release provides the repository source archives generated for the
  `v0.1.0` tag. No additional binary archive is produced.
- Version 0.1.0 does not publish a container image to GHCR, Docker Hub, or another
  registry. Docker Compose builds the `teamsphone-mcp:local` image from the tagged
  source and the pinned dependencies in the Dockerfile.

### Guided setup and safety demonstration

- `init prepare` generates a test certificate, Compose configuration, and independent
  bearer/signing keys with restrictive local permissions and no secret output.
- `init verify` starts the server and proves certificate-authenticated demo-tenant
  connectivity with a real `get-user-voice-config` call.
- Docker-only Ubuntu and PowerShell wrappers provide the same guided workflow from the
  source archive.
- A checked-in, CI-gated safety demonstration shows dry-run, parameter-mismatch refusal,
  exact confirmed execution, audit retrieval, and tokenless session what-if behavior.
- The external release plan covers clean Ubuntu setup, secure VS Code configuration,
  demo-tenant reads and writes, restoration, audit persistence, and release evidence.

### Trust boundaries and known limitations

- **Streamable HTTP uses the sessionful protocol path.** Version 0.1.0 explicitly keeps
  the transport stateful because session ownership, confirmation-token binding, and rate
  limiting depend on `Mcp-Session-Id`. It supports the pre-SEP-2567 session model and
  refuses clients that offer only MCP revision `2026-07-28` or later. Dual-path clients
  must negotiate an earlier supported revision. Stateless HTTP support is tracked in
  [issue #28](https://github.com/mcarlson501/teamsphone-mcp/issues/28).
- **Confirmation-token replay protection is local to one process.** Redeemed token
  identifiers are held in an in-memory cache. Replicas do not share redemption state,
  and restarting a host clears it. Run one host per confirmation-token signing key; do
  not load-balance a shared key across replicas when single-use enforcement matters.
- **PowerShell tools are host-trusted code.** Bundled `run.ps1` files execute in-process
  with FullLanguage access. Static analysis blocks known unsafe constructs, but it is not
  a sandbox. Deploy only the reviewed scripts from this repository and do not accept
  untrusted third-party tool implementations until the S3 constrained-execution
  milestone lands.

See [SECURITY.md](SECURITY.md) for the complete security model, signing-key lifecycle,
and vulnerability reporting process.