#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cast_file="$repo_root/docs/assets/safety-demo.cast"
gif_file="$repo_root/docs/assets/safety-demo.gif"

for command in docker asciinema agg; do
  if ! command -v "$command" >/dev/null 2>&1; then
    echo "error: $command is required." >&2
    exit 1
  fi
done

cd "$repo_root"
docker build --tag teamsphone-mcp:local .
asciinema record \
  --headless \
  --return \
  --overwrite \
  --window-size 100x30 \
  --idle-time-limit 0.5 \
  --title 'TeamsPhone MCP write-safety demonstration' \
  --command 'TEAMSPHONE_MCP_DEMO_SKIP_BUILD=true ./scripts/demo-safety.sh' \
  "$cast_file"
agg \
  --cols 100 \
  --rows 30 \
  --font-size 14 \
  --speed 1.5 \
  --idle-time-limit 0.5 \
  --fps-cap 15 \
  --no-loop \
  "$cast_file" \
  "$gif_file"

echo "Updated $cast_file"
echo "Updated $gif_file"