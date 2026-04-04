FROM python:3.12-slim

# Install system dependencies + Node.js (required for Claude Code CLI) + gh CLI (GitHub operations)
RUN apt-get update && apt-get install -y --no-install-recommends \
    git \
    curl \
    jq \
    nodejs \
    npm \
    && curl -fsSL https://cli.github.com/packages/githubcli-archive-keyring.gpg \
        | dd of=/usr/share/keyrings/githubcli-archive-keyring.gpg \
    && chmod go+r /usr/share/keyrings/githubcli-archive-keyring.gpg \
    && echo "deb [arch=$(dpkg --print-architecture) signed-by=/usr/share/keyrings/githubcli-archive-keyring.gpg] https://cli.github.com/packages stable main" \
        > /etc/apt/sources.list.d/github-cli.list \
    && apt-get update && apt-get install -y --no-install-recommends gh \
    && rm -rf /var/lib/apt/lists/*

# Install Claude Code CLI globally — used by entrypoint.sh for plugin management
RUN npm install -g @anthropic-ai/claude-code

WORKDIR /workspace

COPY requirements.txt /workspace/requirements.txt
RUN pip install --no-cache-dir -r /workspace/requirements.txt

COPY execute_plugin.py /workspace/execute_plugin.py
COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh

# Run as non-root for least-privilege
RUN groupadd -r xianix && useradd -r -g xianix -d /workspace xianix \
    && mkdir -p /workspace/repo \
    && chown -R xianix:xianix /workspace
USER xianix

# Pre-register the official Claude Code marketplace so container starts don't
# spend ~30s cloning it on every run. The cached index lives in the image layer.
RUN claude plugin marketplace add anthropics/claude-plugins-official

ENTRYPOINT ["/entrypoint.sh"]
