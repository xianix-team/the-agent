#!/bin/bash

VAULT_NAME="xianix-kv-agent"
ENV_FILE="$(dirname "$0")/../../TheAgent/.env.production"

if [ ! -f "$ENV_FILE" ]; then
  echo "Error: $ENV_FILE not found"
  exit 1
fi

while IFS= read -r line || [ -n "$line" ]; do
  # Skip empty lines and comments
  [[ -z "$line" || "$line" =~ ^[[:space:]]*# ]] && continue

  key="${line%%=*}"
  value="${line#*=}"
  # Strip inline comments and trailing whitespace
  value="$(echo "$value" | sed 's/[[:space:]]*#.*$//')"

  # Recover soft-deleted secret if it exists, then overwrite with new value
  az keyvault secret recover --vault-name "$VAULT_NAME" --name "$key" 2>/dev/null && \
    echo "Recovered soft-deleted secret: $key" && sleep 5

  echo "Setting secret: $key"
  az keyvault secret set --vault-name "$VAULT_NAME" --name "$key" --value "$value"
done < "$ENV_FILE"

echo "All secrets set in $VAULT_NAME"
