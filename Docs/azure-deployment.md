# Deploying Xianix Agent on an Azure VM

This guide documents how the `xianix-agent` Docker image is deployed on an Azure Linux VM. The VM has **no public IP and no inbound ports open** — all internet traffic is outbound-only via a NAT Gateway. Secrets are stored in **Azure Key Vault** and retrieved at runtime via the VM's **system-assigned managed identity** — no plain-text `.env` file is kept on disk. The agent connects to the Docker socket and automatically pulls and manages `xianix-executor` containers.

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

## Architecture Overview

```
┌─────────────────────────────────────────────────────┐
│  Azure VNet: xianix-agent-vmVNET                    │
│  Subnet: xianix-agent-vmSubnet                      │
│                                                     │
│  NSG: deny all inbound from Internet                │
│                                                     │
│  ┌──────────────────────────────────┐               │
│  │     Azure VM (xianix-agent-vm)   │               │
│  │                                  │               │
│  │  systemd: xianix-agent.service   │               │
│  │        │                         │               │
│  │        ▼                         │               │
│  │  /etc/xianix/start-agent.sh      │               │
│  │        │                         │               │
│  │        │  1. GET token (IMDS)    │               │
│  │        ▼                         │               │
│  │  Azure Key Vault ◄───────────────│── Managed Identity
│  │  xianix-kv-agent                 │               │
│  │        │                         │               │
│  │        │  2. Fetch secrets       │               │
│  │        ▼                         │               │
│  │  docker run xianix-agent ────────│── Docker socket
│  │        │                         │       │       │
│  │        ▼                         │       ▼       │
│  │  xianix-executor (per task)      │  auto-pulled  │
│  └──────────────────────────────────┘               │
│                    │ outbound only                   │
│                    ▼                                 │
│          NAT Gateway (outbound)                     │
└─────────────────────────────────┬───────────────────┘
                                  │
                                  ▼
                           Internet (Docker Hub,
                           Xians platform, APIs)
```

---

## Setup Steps (Already Completed)

### 1. Create the Resource Group

```bash
az group create \
  --name xianix-agent-rg \
  --location norwayeast
```

### 2. Create the VM

```bash
az vm create \
  --resource-group xianix-agent-rg \
  --name xianix-agent-vm \
  --image Ubuntu2204 \
  --size Standard_B2s \
  --admin-username azureuser \
  --generate-ssh-keys
```

### 3. Install Docker

```bash
az vm run-command invoke \
  --resource-group xianix-agent-rg \
  --name xianix-agent-vm \
  --command-id RunShellScript \
  --scripts "
    curl -fsSL https://get.docker.com | sh
    usermod -aG docker azureuser
    systemctl enable docker
    systemctl start docker
  "
```

### 4. Enable System-Assigned Managed Identity on the VM

```bash
az vm identity assign \
  --resource-group xianix-agent-rg \
  --name xianix-agent-vm
```

This returns the identity's `principalId` (object ID), which is used in the next step to set the Key Vault access policy.

### 5. Create the Key Vault

```bash
az keyvault create \
  --name xianix-kv-agent \
  --resource-group xianix-agent-rg \
  --location norwayeast \
  --enable-rbac-authorization false
```

### 6. Grant the VM Identity Access to Secrets

```bash
az keyvault set-policy \
  --name xianix-kv-agent \
  --resource-group xianix-agent-rg \
  --object-id <vm-principal-id> \
  --secret-permissions get list
```

### 7. Pre-pull Docker Images

```bash
az vm run-command invoke \
  --resource-group xianix-agent-rg \
  --name xianix-agent-vm \
  --command-id RunShellScript \
  --scripts "
    docker pull 99xio/xianix-agent:latest
    docker pull 99xio/xianix-executor:latest
  "
```

### 8. Create NAT Gateway for Outbound-Only Internet Access

The VM has no public IP, so a NAT Gateway is required for outbound connectivity to Docker Hub, the Xians platform, and other APIs.

```bash
# Public IP for the NAT Gateway (the VM's egress IP)
az network public-ip create \
  --resource-group xianix-agent-rg \
  --name xianix-agent-nat-ip \
  --location norwayeast \
  --sku Standard \
  --allocation-method Static

# Create the NAT Gateway
az network nat gateway create \
  --resource-group xianix-agent-rg \
  --name xianix-agent-natgw \
  --location norwayeast \
  --public-ip-addresses xianix-agent-nat-ip \
  --idle-timeout 10

# Attach it to the VM's subnet
az network vnet subnet update \
  --resource-group xianix-agent-rg \
  --vnet-name xianix-agent-vmVNET \
  --name xianix-agent-vmSubnet \
  --nat-gateway xianix-agent-natgw
```

### 9. Remove the VM Public IP and Lock Down Inbound Traffic

```bash
# Detach the public IP from the NIC
az network nic ip-config update \
  --resource-group xianix-agent-rg \
  --nic-name xianix-agent-vmVMNic \
  --name ipconfigxianix-agent-vm \
  --remove publicIpAddress

# Remove the auto-created SSH allow rule
az network nsg rule delete \
  --resource-group xianix-agent-rg \
  --nsg-name xianix-agent-vmNSG \
  --name default-allow-ssh

# Deny all inbound from the internet (priority 200, leaving room for allow rules above)
az network nsg rule create \
  --resource-group xianix-agent-rg \
  --nsg-name xianix-agent-vmNSG \
  --name deny-all-inbound \
  --priority 200 \
  --direction Inbound \
  --access Deny \
  --protocol '*' \
  --source-address-prefixes Internet \
  --source-port-ranges '*' \
  --destination-address-prefixes '*' \
  --destination-port-ranges '*'

# Allow SSH from the Azure platform IP (168.63.129.16) so Bastion can reach the VM
# This IP is Azure-internal and is never reachable from the public internet
az network nsg rule create \
  --resource-group xianix-agent-rg \
  --nsg-name xianix-agent-vmNSG \
  --name allow-bastion-ssh \
  --priority 100 \
  --direction Inbound \
  --access Allow \
  --protocol Tcp \
  --source-address-prefixes 168.63.129.16 \
  --source-port-ranges '*' \
  --destination-address-prefixes '*' \
  --destination-port-ranges 22

# Delete the now-unused VM public IP resource
az network public-ip delete \
  --resource-group xianix-agent-rg \
  --name xianix-agent-vmPublicIP
```

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

### Via Azure Portal

Navigate to **Key Vault `xianix-kv-agent` → Secrets → Generate/Import** and add each secret listed above.

---

## How It Works at Runtime

The VM runs `/etc/xianix/start-agent.sh` as a systemd service on boot. The script:

1. Requests a short-lived bearer token from the **Azure Instance Metadata Service (IMDS)** using the VM's managed identity — no stored credentials needed.
2. Calls the Key Vault REST API to fetch each secret.
3. Passes the secrets as environment variables directly to `docker run`.

The script is installed at `/etc/xianix/start-agent.sh` and the service at `/etc/systemd/system/xianix-agent.service`.

---

## Operations Reference

Since the VM has no public IP, all management is done remotely via the Azure CLI. Commands are run using `az vm run-command invoke`. You can optionally define a shell alias to keep examples shorter:

```bash
# Optional shorthand — run once per terminal session
alias vmrun='az vm run-command invoke \
  --resource-group xianix-agent-rg \
  --name xianix-agent-vm \
  --command-id RunShellScript \
  --scripts'
```

The full form of every `vmrun "..."` call below is:

```bash
az vm run-command invoke \
  --resource-group xianix-agent-rg \
  --name xianix-agent-vm \
  --command-id RunShellScript \
  --scripts "..."
```

### Start the Agent (First Run)

After populating the Key Vault secrets, start the agent for the first time:

```bash
az vm run-command invoke \
  --resource-group xianix-agent-rg \
  --name xianix-agent-vm \
  --command-id RunShellScript \
  --scripts "sudo systemctl start xianix-agent"
```

### Check Status and Logs

```bash
# Service status
az vm run-command invoke \
  --resource-group xianix-agent-rg \
  --name xianix-agent-vm \
  --command-id RunShellScript \
  --scripts "sudo systemctl status xianix-agent --no-pager"

# Agent container logs (last 50 lines)
# The 2>&1 is required — the agent writes to both stdout and stderr
az vm run-command invoke \
  --resource-group xianix-agent-rg \
  --name xianix-agent-vm \
  --command-id RunShellScript \
  --scripts "docker logs --tail 50 xianix-agent 2>&1"

# List all running containers (agent + any active executors)
az vm run-command invoke \
  --resource-group xianix-agent-rg \
  --name xianix-agent-vm \
  --command-id RunShellScript \
  --scripts "docker ps"
```

A healthy agent log looks like this — 4 workflow queues registered and workers listening:

```
│ REGISTERED WORKFLOWS (4)                                      │
  Agent:    Xianix AI-DLC Agent
  Workflow: Xianix AI-DLC Agent:Activation Workflow
  ...
✓ Worker listening on queue 'xianix:...:Processing Workflow'
✓ Worker listening on queue 'xianix:...:Activation Workflow'
✓ Worker listening on queue 'xianix:...:Supervisor Workflow'
✓ Worker listening on queue 'xianix:...:Integrator Workflow'
```

### Tail Logs Continuously via Azure Bastion

`az vm run-command invoke` is one-shot and cannot stream. For a live `docker logs -f` experience, connect via **Azure Bastion** (already provisioned, Developer SKU — free):

> **Note:** The Developer SKU only supports browser-based connections. To use the CLI (`az network bastion ssh`), upgrade to the Standard SKU first (see below).

**Browser-based SSH (Developer SKU — no extra setup)**

Go to **[Azure Portal](https://portal.azure.com) → Virtual Machines → `xianix-agent-vm` → Connect → Bastion**, enter `azureuser` and your SSH private key, then run:

```bash
docker logs -f xianix-agent
```

**CLI-based SSH (Standard SKU — ~$0.19/hr)**

```bash
# One-time upgrade
az network bastion update \
  --resource-group xianix-agent-rg \
  --name xianix-agent-bastion \
  --sku Standard \
  --enable-tunneling true

# Install required CLI extensions (one-time, local machine)
az extension add -n bastion
az extension add -n ssh

# Open an interactive SSH session through Bastion
az network bastion ssh \
  --resource-group xianix-agent-rg \
  --name xianix-agent-bastion \
  --target-resource-id "$(az vm show -g xianix-agent-rg -n xianix-agent-vm --query id -o tsv)" \
  --auth-type ssh-key \
  --username azureuser \
  --ssh-key ~/.ssh/id_rsa
```

Once inside the VM:

```bash
docker logs -f xianix-agent
```

Alternatively, poll from your local machine without SSH:

```bash
while true; do
  az vm run-command invoke \
    --resource-group xianix-agent-rg \
    --name xianix-agent-vm \
    --command-id RunShellScript \
    --scripts "docker logs --since 12s xianix-agent 2>&1" \
    --output json 2>/dev/null \
  | python3 -c "
import sys, json
m = json.load(sys.stdin)['value'][0]['message']
print(m[m.find('[stdout]')+8 : m.find('[stderr]')].strip())
"
  sleep 10
done
```

`--since 12s` overlaps the 10-second sleep by 2 seconds to avoid gaps between polls.

### Restart the Agent

```bash
az vm run-command invoke \
  --resource-group xianix-agent-rg \
  --name xianix-agent-vm \
  --command-id RunShellScript \
  --scripts "sudo systemctl restart xianix-agent"
```

### Stop the Agent

```bash
az vm run-command invoke \
  --resource-group xianix-agent-rg \
  --name xianix-agent-vm \
  --command-id RunShellScript \
  --scripts "sudo systemctl stop xianix-agent"
```

### Rotate a Secret

Update the value in Key Vault, then restart the agent — it fetches all secrets fresh on every start:

```bash
az keyvault secret set \
  --vault-name xianix-kv-agent \
  --name ANTHROPIC-API-KEY \
  --value "<new-key>"

az vm run-command invoke \
  --resource-group xianix-agent-rg \
  --name xianix-agent-vm \
  --command-id RunShellScript \
  --scripts "sudo systemctl restart xianix-agent"
```

### Update the Agent Image

```bash
az vm run-command invoke \
  --resource-group xianix-agent-rg \
  --name xianix-agent-vm \
  --command-id RunShellScript \
  --scripts "docker pull 99xio/xianix-agent:latest && sudo systemctl restart xianix-agent"
```

### Update the Executor Image

The agent does **not** auto-pull the executor image — it always uses the locally cached version. To update to a newer image, pull it explicitly on the VM before the next run:

```bash
az vm run-command invoke \
  --resource-group xianix-agent-rg \
  --name xianix-agent-vm \
  --command-id RunShellScript \
  --scripts "docker pull 99xio/xianix-executor:latest"
```

### Start / Stop / Restart the VM Itself

```bash
az vm start   --resource-group xianix-agent-rg --name xianix-agent-vm
az vm stop    --resource-group xianix-agent-rg --name xianix-agent-vm
az vm restart --resource-group xianix-agent-rg --name xianix-agent-vm
```

The agent starts automatically on boot via the systemd service — no manual intervention needed after a VM restart.

---

## Notes

- The VM has no public IP and no open inbound ports. It is unreachable from the internet.
- All outbound traffic (Docker Hub pulls, Xians platform webhooks, API calls) flows through the NAT Gateway.
- To manage the VM, use **Azure Bastion** (`xianix-agent-bastion`) or `az vm run-command invoke` — SSH over the public internet is not possible by design.
- The executor image (`99xio/xianix-executor:latest`) is **not** auto-pulled by the agent — it always uses the locally cached version. The image has been pre-pulled during setup; to update it, use `docker pull 99xio/xianix-executor:latest` on the VM explicitly.
- No secrets are stored on disk. The `/etc/xianix/` directory holds only the startup script.
- SSH keys were auto-generated during VM creation and stored in `~/.ssh/` on the machine that ran `az vm create`.
- The `--restart unless-stopped` Docker flag and the systemd `Restart=on-failure` directive together ensure the agent survives container crashes and VM reboots.
