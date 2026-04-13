# Deploying Xianix Agent on an Azure VM

This guide documents how the `xianix-agent` Docker image is deployed on an Azure Linux VM. The VM has **no public IP and no inbound ports open** — all internet traffic is outbound-only via a NAT Gateway.

Secrets are stored in **Azure Key Vault** and retrieved at runtime via the VM's **system-assigned managed identity** — no plain-text `.env` file is kept on disk. 

The agent connects to the Docker socket and automatically pulls and manages `xianix-executor` containers.

## Operations Reference

Since the VM has no public IP, all management is done remotely via the Azure CLI. Commands are run using `az vm run-command invoke`. You can optionally define a shell alias to keep examples shorter:

```bash
az vm run-command invoke \
  --resource-group xianix-agent-rg \
  --name xianix-agent-vm \
  --command-id RunShellScript \
  --scripts "..."
```

### Azure Bastion

**Browser-based SSH (Developer SKU — no extra setup)**

Go to **[Azure Portal](https://portal.azure.com) → Virtual Machines → `xianix-agent-vm` → Connect → Bastion**, enter `azureuser` and your SSH private key, then run:

### Start the Agent

After populating the Key Vault secrets, start the agent for the first time:

From Bastion terminal:

```bash
sudo systemctl start xianix-agent
```

OR from CLI:

```bash
az vm run-command invoke \
  --resource-group xianix-agent-rg \
  --name xianix-agent-vm \
  --command-id RunShellScript \
  --scripts "sudo systemctl start xianix-agent"
```

### Check Status and Logs

```bash
docker logs -f xianix-agent
```

### Restart the Agent

```bash
sudo systemctl restart xianix-agent
```

### Stop the Agent

```bash
sudo systemctl stop xianix-agent
```

### Rotate a Secret

Update the value in Key Vault, then restart the agent — it fetches all secrets fresh on every start:

From CLI:

```bash
az keyvault secret set \
  --vault-name xianix-kv-agent \
  --name ANTHROPIC-API-KEY \
  --value "<new-key>"
```

Then on Bastion console:

```bash
sudo systemctl restart xianix-agent
```

### Update the Executor Image

The agent does **not** auto-pull the executor image — it always uses the locally cached version. To update to a newer image, pull it explicitly on the VM before the next run:

```bash
docker pull 99xio/xianix-executor:latest
```

### Update the Agent Image

```bash
docker pull 99xio/xianix-agent:latest
```

### Restart after docker pull

```bash
sudo systemctl restart xianix-agent
```

The agent starts automatically on boot via the systemd service — no manual intervention needed after a VM restart.

---

## Populating Secrets

Secrets are stored in Key Vault and read at startup. Use the Azure Portal or CLI to set each one.

### Via CLI

```bash
az keyvault secret set --vault-name xianix-kv-agent --name XIANS-SERVER-URL    --value "https://app.xians.ai"
az keyvault secret set --vault-name xianix-kv-agent --name XIANS-API-KEY        --value "<your-xians-api-key>"
az keyvault secret set --vault-name xianix-kv-agent --name ANTHROPIC-API-KEY    --value "<your-anthropic-api-key>"
az keyvault secret set --vault-name xianix-kv-agent --name GITHUB-TOKEN         --value "<your-github-token>"
az keyvault secret set --vault-name xianix-kv-agent --name AZURE-DEVOPS-TOKEN   --value "<your-ado-token>"
az keyvault secret set --vault-name xianix-kv-agent --name EXECUTOR-IMAGE       --value "99xio/xianix-executor:latest"
az keyvault secret set --vault-name xianix-kv-agent --name CONTAINER-MEMORY-MB  --value "1024"
az keyvault secret set --vault-name xianix-kv-agent --name CONTAINER-CPU-COUNT  --value "1"
```

### Working with KeyVault secrets

```bash
# List all secret names
az keyvault secret list --vault-name xianix-kv-agent --query "[].name" -o table

# Read a secret value
az keyvault secret show --vault-name xianix-kv-agent --name AZURE-DEVOPS-TOKEN --query "value" -o tsv

# Add or update a secret
az keyvault secret set --vault-name xianix-kv-agent --name MY-NEW-SECRET --value "<value>"

# Delete a secret
az keyvault secret delete --vault-name xianix-kv-agent --name AZURE-DEVOPS-TOKEN
```

Secret names use **dashes** (e.g. `MY-NEW-SECRET`) because Azure Key Vault doesn't allow underscores. The startup script passes them as-is and the agent resolves both forms transparently (e.g. `MY-NEW-SECRET` and `MY_NEW_SECRET` both work). Any new secret added to the vault is picked up on the next restart — no script changes needed.

After adding or changing a secret, restart the agent:

```bash
sudo systemctl restart xianix-agent
```

### Via Azure Portal

Navigate to **Key Vault `xianix-kv-agent` → Secrets → Generate/Import** and add each secret listed above.

### Restart after secret updates

```bash
sudo systemctl restart xianix-agent
```

---

## Provisioned Resources

| Resource | Value |
|---|---|
| Resource Group | `xianix-agent-rg` |
| Location | `norwayeast` |
| VM Name | `xianix-agent-vm` |
| VM Size | `Standard_B2s` |
| OS | Ubuntu 22.04 LTS |
| Public IP | None (no inbound access) |
| Admin User | `azureuser` |
| Key Vault | `xianix-kv-agent` |
| NAT Gateway | `xianix-agent-natgw` |
| Bastion | `xianix-agent-bastion` (Developer SKU — free) |
| NSG | `xianix-agent-vmNSG` (deny all inbound from Internet; allow port 22 from Azure platform) |

---

## Notes

- The startup script and systemd service are version-controlled in [`Scripts/vm/`](../Scripts/vm/). Always update the repo copies when changing the live files, and vice versa.
- The VM has no public IP and no open inbound ports. It is unreachable from the internet.
- All outbound traffic (Docker Hub pulls, Xians platform webhooks, API calls) flows through the NAT Gateway.
- To manage the VM, use **Azure Bastion** (`xianix-agent-bastion`) or `az vm run-command invoke` — SSH over the public internet is not possible by design.
- The executor image (`99xio/xianix-executor:latest`) is **not** auto-pulled by the agent — it always uses the locally cached version. The image has been pre-pulled during setup; to update it, use `docker pull 99xio/xianix-executor:latest` on the VM explicitly.
- No secrets are stored on disk. The `/etc/xianix/` directory holds only the startup script.
- SSH keys were auto-generated during VM creation and stored in `~/.ssh/` on the machine that ran `az vm create`.
- The `--restart unless-stopped` Docker flag and the systemd `Restart=on-failure` directive together ensure the agent survives container crashes and VM reboots.
