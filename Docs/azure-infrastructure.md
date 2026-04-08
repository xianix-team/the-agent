
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