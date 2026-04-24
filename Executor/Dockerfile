FROM python:3.12-slim

# System deps + Node 20 (npx/MCP) + git + GitHub CLI (gh) + Azure CLI + azure-devops extension (az devops).
# Azure CLI is installed from Microsoft's bookworm repo (no trixie dist yet); compatible with current slim base.
RUN apt-get update && apt-get install -y --no-install-recommends \
    git \
    curl \
    jq \
    ca-certificates \
    gnupg \
    && mkdir -p /etc/apt/keyrings \
    && curl -fsSL https://deb.nodesource.com/gpgkey/nodesource-repo.gpg.key \
        | gpg --dearmor -o /etc/apt/keyrings/nodesource.gpg \
    && echo "deb [signed-by=/etc/apt/keyrings/nodesource.gpg] https://deb.nodesource.com/node_20.x nodistro main" \
        > /etc/apt/sources.list.d/nodesource.list \
    && curl -fsSL https://cli.github.com/packages/githubcli-archive-keyring.gpg \
        | tee /usr/share/keyrings/githubcli-archive-keyring.gpg >/dev/null \
    && chmod go+r /usr/share/keyrings/githubcli-archive-keyring.gpg \
    && echo "deb [arch=$(dpkg --print-architecture) signed-by=/usr/share/keyrings/githubcli-archive-keyring.gpg] https://cli.github.com/packages stable main" \
        > /etc/apt/sources.list.d/github-cli.list \
    && curl -sLS https://packages.microsoft.com/keys/microsoft.asc \
        | gpg --dearmor -o /etc/apt/keyrings/microsoft.gpg \
    && chmod go+r /etc/apt/keyrings/microsoft.gpg \
    && echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/microsoft.gpg] https://packages.microsoft.com/repos/azure-cli/ bookworm main" \
        > /etc/apt/sources.list.d/azure-cli.list \
    && apt-get update && apt-get install -y --no-install-recommends nodejs gh azure-cli \
    && az extension add --name azure-devops --yes \
    && rm -rf /var/lib/apt/lists/*

RUN npm install -g @anthropic-ai/claude-code

WORKDIR /workspace

COPY requirements.txt /workspace/requirements.txt
RUN pip install --no-cache-dir -r /workspace/requirements.txt

COPY execute_plugin.py /workspace/execute_plugin.py
COPY _common.sh /workspace/_common.sh
COPY prepare_repo.sh /workspace/prepare_repo.sh
COPY run_prompt.sh /workspace/run_prompt.sh
COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh /workspace/prepare_repo.sh /workspace/run_prompt.sh

RUN groupadd -r xianix && useradd -r -g xianix -d /workspace xianix \
    && mkdir -p /workspace/repo \
    && chown -R xianix:xianix /workspace
USER xianix

ENTRYPOINT ["/entrypoint.sh"]
