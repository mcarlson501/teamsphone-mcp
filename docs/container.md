# Run TeamsPhone MCP with Docker Compose

The container packages the .NET host, PowerShell 7.4.6, MicrosoftTeams 7.8.0,
and all validated tool manifests. It supports Linux AMD64 and ARM64, runs as a
non-root user, binds to localhost by default, and starts in `whatif` mode.

## Prerequisites

- Docker Engine or Docker Desktop with Compose v2
- An Entra app and password-protected PFX from
  [setup-entra-app.md](setup-entra-app.md)

## Configure

Copy the environment template and fill every blank value:

```bash
cp .env.example .env
```

Generate independent bearer and confirmation-token keys:

```bash
openssl rand -base64 32
openssl rand -base64 32
```

Set `TEAMSPHONE_MCP_CERTIFICATE_PATH` to the absolute host path of the PFX.
Compose mounts it read-only at a fixed container path. The PFX and its password
are never copied into the image. Tool calls use `credentialRef: "default"` and
the tenant ID from `TEAMSPHONE_MCP_TENANT_ID`.

Validate the resolved configuration before starting:

```bash
docker compose config --quiet
```

## Start

```bash
docker compose up --build --detach
docker compose ps
docker compose logs --tail 50 teamsphone-mcp
```

The MCP endpoint is `http://127.0.0.1:5199/mcp` unless
`TEAMSPHONE_MCP_PORT` changes it. Add the bearer token from `.env` to the MCP
client's `Authorization` header.

An unauthenticated request must return `401`:

```bash
curl -s -o /dev/null -w '%{http_code}\n' -X POST http://127.0.0.1:5199/mcp \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}'
```

## Data and updates

Audit records persist in the named `audit` volume. Back it up according to the
retention and evidence requirements in [audit.md](audit.md).

Rebuild after pulling an update:

```bash
docker compose pull --ignore-buildable
docker compose build --pull
docker compose up --detach
```

Stop the server without deleting audit data:

```bash
docker compose down
```

Do not use `docker compose down --volumes` unless the audit volume has been
backed up and intentional deletion is approved.

## Pinned runtime

| Component | Version |
| --------- | ------- |
| .NET SDK | 8.0.422 |
| ASP.NET runtime | 8.0.28 |
| PowerShell | 7.4.6 |
| MicrosoftTeams | 7.8.0 |

Base images and downloaded PowerShell/module artifacts are checksum-pinned in
the [Dockerfile](../Dockerfile). Upgrade them only with the offline and live
acceptance suites described in [testing.md](testing.md).