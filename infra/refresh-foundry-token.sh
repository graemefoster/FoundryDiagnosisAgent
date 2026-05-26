#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
ENV_FILE="$REPO_ROOT/infra/.env"

if ! command -v az >/dev/null 2>&1; then
  echo "Azure CLI (az) not found on PATH" >&2
  exit 1
fi

TOKEN="$(az account get-access-token --resource https://ai.azure.com --query accessToken -o tsv)"
if [[ -z "$TOKEN" ]]; then
  echo "Failed to fetch token" >&2
  exit 1
fi

cat > "$ENV_FILE" <<EOF
FOUNDRY_TOKEN=$TOKEN
EOF

echo "Updated $ENV_FILE with fresh FOUNDRY_TOKEN for REST Client."
