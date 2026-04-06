# Deploying Xianix Agent on an Azure VM

This guide walks through running the `xianix-agent` Docker image on an Azure Linux VM. The agent connects to the Docker socket and automatically pulls and manages `xianix-executor` containers at runtime — no manual setup of the executor image is required.

---

## Prerequisites

- An Azure VM running Ubuntu 22.04 (or later)
- Docker installed on the VM
- Outbound internet access to Docker Hub

---

## 1. Provision the Azure VM

Create a VM via the Azure Portal or CLI. The following CLI example uses a minimal Ubuntu 22.04 setup:

```bash
az vm create \
  --resource-group <your-resource-group> \
  --name xianix-agent-vm \
  --image Ubuntu2204 \
  --size Standard_B2s \
  --admin-username azureuser \
  --generate-ssh-keys
```

Open port 443 if the agent needs to reach the Xians platform over HTTPS (usually outbound-only, so no inbound rule is needed).

---

## 2. Install Docker

SSH into the VM and install Docker:

```bash
ssh azureuser@<vm-public-ip>

curl -fsSL https://get.docker.com | sudo sh
sudo usermod -aG docker azureuser
newgrp docker
```

Verify:

```bash
docker version
```

---

## 3. Create the Environment File

Create `/etc/xianix/.env` with the required configuration:

```bash
sudo mkdir -p /etc/xianix
sudo nano /etc/xianix/.env
```

Populate it with the following values (replace placeholders with real values):

```env
# Xians Platform
XIANS_SERVER_URL=https://app.xians.ai
XIANS_API_KEY=<your-xians-api-key>

# LLM
ANTHROPIC_API_KEY=<your-anthropic-api-key>

# Source control platform token (provide whichever you use)
GITHUB_TOKEN=<your-github-token>
AZURE_DEVOPS_TOKEN=<your-azure-devops-token>

# Executor image — pulled automatically by the agent on first use
EXECUTOR_IMAGE=99xio/xianix-executor:latest

# Container resource limits
CONTAINER_MEMORY_MB=1024
CONTAINER_CPU_COUNT=1
```

Restrict file permissions:

```bash
sudo chmod 600 /etc/xianix/.env
```

---

## 4. Pull and Run the Agent

Pull the agent image from Docker Hub:

```bash
docker pull 99xio/xianix-agent:latest
```

Run it with the Docker socket mounted so it can manage executor containers:

```bash
docker run -d \
  --name xianix-agent \
  --restart unless-stopped \
  -v /var/run/docker.sock:/var/run/docker.sock \
  --env-file /etc/xianix/.env \
  99xio/xianix-agent:latest
```

The agent will automatically pull `99xio/xianix-executor:latest` from Docker Hub the first time it needs to spawn an executor container. No manual executor setup is required.

---

## 5. Verify the Deployment

Check that the agent container is running:

```bash
docker ps
docker logs xianix-agent
```

You should see the agent connect to the Xians platform and begin polling for work. When the first task arrives, Docker will pull the executor image and spin up an isolated container for it.

---

## Updating the Agent

To update to a newer image version:

```bash
docker pull 99xio/xianix-agent:latest
docker stop xianix-agent && docker rm xianix-agent
docker run -d \
  --name xianix-agent \
  --restart unless-stopped \
  -v /var/run/docker.sock:/var/run/docker.sock \
  --env-file /etc/xianix/.env \
  99xio/xianix-agent:latest
```

---

## Notes

- The `--restart unless-stopped` flag ensures the agent restarts automatically if the VM reboots.
- The executor image (`99xio/xianix-executor:latest`) is pulled on demand — it does not need to be pre-pulled manually.
- Keep `/etc/xianix/.env` secure; it contains API keys and platform tokens.
