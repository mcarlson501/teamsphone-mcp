#!/usr/bin/env bash
# Runs a live-tenant test with the credential environment the host expects.
#
# The configuration keys the host reads contain a hyphen
# (Credentials__dev-tenant__ClientId), which zsh/bash cannot express as a shell
# variable. This wrapper translates the shell-safe TP_CRED_* values from
# .env.integration into those keys using env(1), which has no such restriction.
#
# Usage:
#   set -a; source .env.integration; set +a
#   ./scripts/live-test.sh --filter FullyQualifiedName~MoveNumberIntegration
set -euo pipefail

ref="${TEAMSPHONE_MCP_IT_CREDENTIAL_REF:-dev-tenant}"

for required in TP_CRED_TENANT_ID TP_CRED_CLIENT_ID TP_CRED_CERT_PATH; do
  if [[ -z "${!required:-}" ]]; then
    echo "error: $required is not set. Source .env.integration first." >&2
    exit 1
  fi
done

exec env \
  "Credentials__${ref}__TenantId=${TP_CRED_TENANT_ID}" \
  "Credentials__${ref}__ClientId=${TP_CRED_CLIENT_ID}" \
  "Credentials__${ref}__CertificatePath=${TP_CRED_CERT_PATH}" \
  "Credentials__${ref}__CertificatePasswordEnvVar=TEAMSPHONE_MCP_DEV_PFX_PASSWORD" \
  ASPNETCORE_ENVIRONMENT=Development \
  dotnet test tests/unit "$@"
