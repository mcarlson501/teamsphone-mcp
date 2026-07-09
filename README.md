# teamsphone-mcp

An open-source **MCP server** that exposes Microsoft Teams Phone administration
operations (MACD — Move / Add / Change / Delete — plus read/diagnostic tools) as
MCP tools. It is **tenant-agnostic**: it contains zero customer data and zero
baked-in credentials. Tenant identity and credentials are supplied per session/call.

See [`teamsphone-mcp-build-spec`](./teamsphone-mcp-build-spec) for the full project
specification and roadmap.

> **Status: Milestone M1 (in progress).** The repository now includes the M0 host
> skeleton plus an initial M1 foundation: a YAML manifest catalog, schema validation,
> a starter `tools/_template`, and a mock write tool that demonstrates dry-run +
> confirmation-token flow. PowerShell execution lands in M2.

## Layout

```
src/TeamsPhoneMcp.Host/   ASP.NET Core entrypoint, transports, auth + logging middleware
src/TeamsPhoneMcp.Core/   Tools + central registration seam (registerAllTools pattern)
tests/unit/               xUnit tests (auth, ping, registration)
```

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- (For manual testing) [MCP Inspector](https://github.com/modelcontextprotocol/inspector):
  `npx @modelcontextprotocol/inspector`

## Build & test

```bash
dotnet build TeamsPhoneMcp.sln
dotnet test  TeamsPhoneMcp.sln
```

## Running the server

The host selects its transport from the command line / environment:

| Transport            | How to select                                  | Auth                         |
| -------------------- | ---------------------------------------------- | ---------------------------- |
| Streamable HTTP (default) | *(no flag)* — serves `/mcp`               | Static token **required**    |
| stdio                | `--stdio` **or** `TEAMSPHONE_MCP_STDIO=true`   | Locally trusted (no token)   |

### Configuration

| Setting                        | Env var / config key                          | Purpose                                   |
| ------------------------------ | --------------------------------------------- | ----------------------------------------- |
| Client auth token (HTTP)       | `TEAMSPHONE_MCP_BEARER_TOKEN` (or `Auth:BearerToken`) | Static token clients must present    |
| Transport = stdio              | `TEAMSPHONE_MCP_STDIO=true` (or `--stdio`)    | Use stdio instead of HTTP                 |
| HTTP bind address              | `ASPNETCORE_URLS`                             | e.g. `http://127.0.0.1:5199`              |

The bearer token is **read from configuration/environment only** — it is never
hardcoded and never written to logs. If no token is configured, the HTTP transport
**fails closed**: every request to `/mcp` is rejected with `401`.

### HTTP transport

```bash
export TEAMSPHONE_MCP_BEARER_TOKEN='choose-a-strong-token'
export ASPNETCORE_URLS='http://127.0.0.1:5199'
dotnet run --project src/TeamsPhoneMcp.Host
```

### stdio transport

```bash
dotnet run --project src/TeamsPhoneMcp.Host -- --stdio
```

## Connecting with MCP Inspector (manual acceptance harness)

### Over HTTP

1. Start the HTTP server as above (with a bearer token set).
2. Launch Inspector: `npx @modelcontextprotocol/inspector`.
3. Choose transport **Streamable HTTP**, URL `http://127.0.0.1:5199/mcp`.
4. Under **Authentication**, add an `Authorization` header using the `Bearer`
   scheme followed by your configured token.
5. Connect, then **List Tools** → you should see `ping` and
   `mock-write-user-policy`.
6. Call `mock-write-user-policy` once without `dryRun:false` to get a
   `confirmationToken`, then call again with `dryRun:false` and that token to
   execute the mocked write.

### Over stdio

1. Launch Inspector: `npx @modelcontextprotocol/inspector`.
2. Choose transport **STDIO** with:
   - Command: `dotnet`
   - Arguments: `run --project src/TeamsPhoneMcp.Host -- --stdio`
3. Connect, then **List Tools** → `ping` and `mock-write-user-policy`.

## Verifying the unauthenticated rejection (acceptance criterion)

With the HTTP server running and a token configured, an unauthenticated request to
`/mcp` must return `401` with no tool listing:

```bash
curl -s -o /dev/null -w '%{http_code}\n' -X POST http://127.0.0.1:5199/mcp \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}'
# → 401
```

## License

[Apache 2.0](./LICENSE).
