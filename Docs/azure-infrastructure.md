
# Azure Infrastructure

## Infrastructure Setup Steps (Already Completed)

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

### 3. Install Docker and jq

```bash
az vm run-command invoke \
  --resource-group xianix-agent-rg \
  --name xianix-agent-vm \
  --command-id RunShellScript \
  --scripts "
    apt-get update -qq
    apt-get install -y -qq jq
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

## How It Works at Runtime

The VM runs `/etc/xianix/start-agent.sh` as a systemd service on boot. The script:

1. Requests a short-lived bearer token from the **Azure Instance Metadata Service (IMDS)** using the VM's managed identity — no stored credentials needed.
2. Calls the Key Vault REST API to fetch each secret.
3. Passes the secrets as environment variables directly to `docker run`.

The script is installed at `/etc/xianix/start-agent.sh` and the service at `/etc/systemd/system/xianix-agent.service`.

Both files are version-controlled in this repository under [`Scripts/vm/`](../Scripts/vm/).

---

## Installing the Startup Scripts on a New VM

After the infrastructure is provisioned (see [azure-infrastructure.md](azure-infrastructure.md)), Docker is installed, and the Key Vault secrets are populated, install the startup script and systemd service on the VM.

### Option A — Via Azure Bastion

SSH into the VM through Bastion and run:

```bash
# Create the script directory
sudo mkdir -p /etc/xianix

# Copy the startup script (paste contents or scp via Bastion)
sudo tee /etc/xianix/start-agent.sh < start-agent.sh > /dev/null
sudo chmod +x /etc/xianix/start-agent.sh

# Copy the systemd unit
sudo tee /etc/systemd/system/xianix-agent.service < xianix-agent.service > /dev/null

# Enable the service so it starts on boot
sudo systemctl daemon-reload
sudo systemctl enable xianix-agent
```

### Option B — Via `az vm run-command invoke`

Use the Azure CLI to push the files remotely. Replace the heredoc contents with the latest versions from `Scripts/vm/`.

```bash
# Install start-agent.sh
az vm run-command invoke \
  --resource-group xianix-agent-rg \
  --name xianix-agent-vm \
  --command-id RunShellScript \
  --scripts "
    mkdir -p /etc/xianix
    cat > /etc/xianix/start-agent.sh << 'SCRIPT_EOF'
$(cat Scripts/vm/start-agent.sh)
SCRIPT_EOF
    chmod +x /etc/xianix/start-agent.sh
  "

# Install xianix-agent.service
az vm run-command invoke \
  --resource-group xianix-agent-rg \
  --name xianix-agent-vm \
  --command-id RunShellScript \
  --scripts "
    cat > /etc/systemd/system/xianix-agent.service << 'UNIT_EOF'
$(cat Scripts/vm/xianix-agent.service)
UNIT_EOF
    systemctl daemon-reload
    systemctl enable xianix-agent
  "
```

### Verify installation

```bash
az vm run-command invoke \
  --resource-group xianix-agent-rg \
  --name xianix-agent-vm \
  --command-id RunShellScript \
  --scripts "
    echo '--- start-agent.sh ---'
    cat /etc/xianix/start-agent.sh
    echo ''
    echo '--- xianix-agent.service ---'
    cat /etc/systemd/system/xianix-agent.service
    echo ''
    echo '--- systemd status ---'
    systemctl is-enabled xianix-agent
  "
```

---

## Retrieving Scripts from an Existing VM

If you need to capture the live scripts from the current VM (to verify or update the repo copies):

```bash
# Dump start-agent.sh
az vm run-command invoke \
  --resource-group xianix-agent-rg \
  --name xianix-agent-vm \
  --command-id RunShellScript \
  --scripts "cat /etc/xianix/start-agent.sh"

# Dump xianix-agent.service
az vm run-command invoke \
  --resource-group xianix-agent-rg \
  --name xianix-agent-vm \
  --command-id RunShellScript \
  --scripts "cat /etc/systemd/system/xianix-agent.service"
```

Compare the output with the files in `Scripts/vm/` and update either side as needed.

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
