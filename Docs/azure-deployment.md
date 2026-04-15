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

E.g.,

```bash
az vm run-command invoke \
  --resource-group xianix-agent-rg \
  --name xianix-agent-vm \
  --command-id RunShellScript \
  --scripts "sudo systemctl restart xianix-agent"
```

### Azure Bastion

Go to **[Azure Portal](https://portal.azure.com) → Virtual Machines → `xianix-agent-vm` → Connect → Bastion**, enter `azureuser` and your SSH private key, then run the command.

E.g.,

```bash
sudo systemctl start xianix-agent
```

## Check Status and Logs

```bash
docker logs -f xianix-agent
```

## Agent Lifecycle

```bash
sudo systemctl start xianix-agent
sudo systemctl restart xianix-agent
sudo systemctl stop xianix-agent
```

## Deploying a new Version

The agent does **not** auto-pull the images — it always uses the locally cached version. To update to a newer image, pull it explicitly on the VM before the next run:

```bash
docker pull 99xio/xianix-executor:latest
docker pull 99xio/xianix-agent:latest
```

Restart after docker pull:

```bash
sudo systemctl restart xianix-agent
```

The agent starts automatically on boot via the systemd service — no manual intervention needed after a VM restart.

---

## Tenant-Scoped Secrets

The agent supports per-tenant secrets so that each tenant can use its own platform token (e.g. GitHub PAT, Azure DevOps PAT). At runtime the agent looks up the tenant-scoped key first and falls back to the global key if no override exists.

### Naming Convention

The tenant-scoped key is built by upper-casing the tenant ID, replacing every non-alphanumeric character with a dash, then appending the base secret name:

```
<TENANT-ID>-<SECRET-NAME>
```

| Tenant ID     | Base Secret        | Key Vault Secret Name       |
|---------------|--------------------|-----------------------------|
| `happyinc`    | `GITHUB-TOKEN`     | `HAPPYINC-GITHUB-TOKEN`     |
| `happy_inc`   | `GITHUB-TOKEN`     | `HAPPY-INC-GITHUB-TOKEN`    |
| `Acme.Corp`   | `AZURE-DEVOPS-TOKEN` | `ACME-CORP-AZURE-DEVOPS-TOKEN` |

If no tenant-scoped secret is found, the agent falls back to the global secret (e.g. `GITHUB-TOKEN`).

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
