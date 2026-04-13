#!/bin/bash

VAULT_NAME="xianix-kv-agent"

SECRETS=(
  "ANTHROPIC-API-KEY"
  "CONTAINER-CPU-COUNT"
  "CONTAINER-MEMORY-MB"
  "EXECUTOR-IMAGE"
  "XIANS-API-KEY"
  "XIANS-SERVER-URL"
)

for secret in "${SECRETS[@]}"; do
  echo "Deleting secret: $secret"
  az keyvault secret delete --vault-name "$VAULT_NAME" --name "$secret" 2>/dev/null
  echo "Purging secret: $secret"
  az keyvault secret purge --vault-name "$VAULT_NAME" --name "$secret" 2>/dev/null
done

echo "All secrets deleted and purged from $VAULT_NAME"
