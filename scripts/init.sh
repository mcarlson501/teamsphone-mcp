#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
image="teamsphone-mcp:local"
environment_file="$repo_root/.env"
certificate_directory="${TEAMSPHONE_MCP_INIT_CERTIFICATE_DIRECTORY:-$HOME/.config/teamsphone-mcp}"

usage() {
  cat <<'EOF'
Usage:
  ./scripts/init.sh prepare --tenant-id <guid> --client-id <guid> [--force]
  ./scripts/init.sh verify --user-upn <demo-user-upn>

Environment overrides:
  TEAMSPHONE_MCP_INIT_CERTIFICATE_DIRECTORY Private certificate directory
  TEAMSPHONE_MCP_INIT_SKIP_BUILD=true        Reuse an existing local image
EOF
}

if [[ $# -eq 0 || "$1" == --help || "$1" == -h || "$1" == help ]]; then
  usage
  exit 0
fi

action="$1"
shift

if [[ "$action" != prepare && "$action" != verify ]]; then
  echo "error: expected 'prepare' or 'verify'." >&2
  usage >&2
  exit 2
fi

if [[ "${TEAMSPHONE_MCP_INIT_SKIP_BUILD:-false}" != true ]]; then
  docker build --tag "$image" "$repo_root"
elif ! docker image inspect "$image" >/dev/null 2>&1; then
  echo "error: $image does not exist and TEAMSPHONE_MCP_INIT_SKIP_BUILD=true." >&2
  exit 1
fi

run_init() {
  local docker_args=(
    run
    --rm
    --user "$(id -u):$(id -g)"
    --env HOME=/tmp
  )
  if [[ "$action" == verify ]]; then
    docker_args+=(--network host)
  fi
  docker_args+=(
    --volume "$repo_root:$repo_root"
    --volume "$certificate_directory:$certificate_directory"
    --workdir "$repo_root"
    --entrypoint dotnet
    "$image"
    /app/TeamsPhoneMcp.Host.dll
    init
  )

  docker "${docker_args[@]}" "$@"
}

if [[ "$action" == prepare ]]; then
  mkdir -p "$certificate_directory"
  chmod 700 "$certificate_directory"
  run_init prepare "$@" \
    --project-directory "$repo_root" \
    --env-file "$environment_file" \
    --certificate-directory "$certificate_directory"
  exit
fi

if [[ "$(uname -s)" != Linux ]]; then
  echo "error: Docker-based verify uses host networking and is supported on Linux." >&2
  echo "Use: dotnet run --project src/TeamsPhoneMcp.Host -- init verify --user-upn <upn>" >&2
  exit 1
fi

docker compose --project-directory "$repo_root" --env-file "$environment_file" config --quiet
docker compose --project-directory "$repo_root" --env-file "$environment_file" up --detach --no-build
run_init verify "$@" \
  --no-start \
  --project-directory "$repo_root" \
  --env-file "$environment_file"