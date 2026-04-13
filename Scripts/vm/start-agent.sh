#!/usr/bin/env bash
set -euo pipefail

VAULT_NAME="xianix-kv-agent"
REQUIRED_VARS=("XIANS-SERVER-URL" "XIANS-API-KEY" "ANTHROPIC-API-KEY")

# ── Acquire a bearer token from the Azure Instance Metadata Service (IMDS) ──
# The VM's system-assigned managed identity authenticates automatically;
# no client-id or stored credential is needed.
TOKEN=$(curl -sf \
  'http://169.254.169.254/metadata/identity/oauth2/token?api-version=2018-02-01&resource=https://vault.azure.net' \
  -H 'Metadata: true' | jq -r '.access_token')

if [ -z "$TOKEN" ] || [ "$TOKEN" = "null" ]; then
  echo "ERROR: Failed to acquire IMDS token. Is the VM's managed identity enabled?" >&2
  exit 1
fi

# ── List every secret in the vault and fetch its value ──
# Secret names are passed as-is from Key Vault (e.g. ANTHROPIC-API-KEY).
# The .NET app handles both dashes and underscores transparently.
SECRET_NAMES=$(curl -sf \
  "https://${VAULT_NAME}.vault.azure.net/secrets?api-version=7.4" \
  -H "Authorization: Bearer ${TOKEN}" | jq -r '.value[].id' | xargs -n1 basename)

declare -A SECRETS
DOCKER_ENV_ARGS=()

for kv_name in $SECRET_NAMES; do
  value=$(curl -sf \
    "https://${VAULT_NAME}.vault.azure.net/secrets/${kv_name}?api-version=7.4" \
    -H "Authorization: Bearer ${TOKEN}" | jq -r '.value // empty')

  SECRETS["$kv_name"]="$value"
  DOCKER_ENV_ARGS+=(-e "${kv_name}=${value}")
  echo "  loaded: ${kv_name}"
done

# ── Fail fast if any required secret is missing ──
for var in "${REQUIRED_VARS[@]}"; do
  if [ -z "${SECRETS[$var]:-}" ]; then
    echo "ERROR: Required secret $var is empty or missing in Key Vault." >&2
    exit 1
  fi
done

# ── Stop and remove any existing agent container ──
docker rm -f xianix-agent 2>/dev/null || true

# ── Start the agent ──
docker run -d \
  --name xianix-agent \
  --restart unless-stopped \
  -v /var/run/docker.sock:/var/run/docker.sock \
  "${DOCKER_ENV_ARGS[@]}" \
  99xio/xianix-agent:latest
