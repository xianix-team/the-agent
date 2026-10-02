FROM python:3.12-slim-bookworm

# OpenCode-only executor. Shared prepare/isolation/runtime plumbing.
# libicu72: required by any .NET SDK mise may install for repo builds.
# No Claude marketplace / plugin-compat adapter in this image — prompts run
# directly via `opencode run`.
RUN apt-get update && apt-get install -y --no-install-recommends \
    git \
    curl \
    jq \
    universal-ctags \
    ca-certificates \
    gnupg \
    libicu72 \
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
    && curl -sL https://packages.microsoft.com/keys/microsoft.asc \
        | gpg --dearmor -o /etc/apt/keyrings/microsoft.gpg \
    && chmod go+r /etc/apt/keyrings/microsoft.gpg \
    && echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/microsoft.gpg] https://packages.microsoft.com/repos/azure-cli/ bookworm main" \
        > /etc/apt/sources.list.d/azure-cli.list \
    && apt-get update && apt-get install -y --no-install-recommends nodejs gh azure-cli \
    && az extension add --name azure-devops --yes \
    && rm -rf /var/lib/apt/lists/*

ARG OPENCODE_VERSION=1.18.31
RUN npm install -g "opencode-ai@${OPENCODE_VERSION}" \
    && rm -rf /root/.npm

WORKDIR /workspace

# mise — user-space SDKs from repo version files (.tool-versions, global.json, …).
ARG MISE_VERSION=v2026.8.0
RUN set -eux; \
    case "$(dpkg --print-architecture)" in \
        amd64) mise_arch=x64 ;; \
        arm64) mise_arch=arm64 ;; \
        *) echo "unsupported architecture for mise: $(dpkg --print-architecture)" >&2; exit 1 ;; \
    esac; \
    tarball="mise-${MISE_VERSION}-linux-${mise_arch}.tar.gz"; \
    base="https://github.com/jdx/mise/releases/download/${MISE_VERSION}"; \
    mkdir -p /tmp/mise-dl; cd /tmp/mise-dl; \
    curl -fsSL "${base}/${tarball}" -o "${tarball}"; \
    curl -fsSL "${base}/SHASUMS256.txt" -o SHASUMS256.txt; \
    grep " \./${tarball}\$" SHASUMS256.txt | sha256sum -c -; \
    tar -xzf "${tarball}"; \
    mv mise/bin/mise /usr/local/bin/mise; \
    chmod +x /usr/local/bin/mise; \
    cd /; rm -rf /tmp/mise-dl; \
    mise --version

COPY execute_opencode.py /workspace/execute_opencode.py
COPY host_context.py /workspace/host_context.py
COPY _common.sh /workspace/_common.sh
COPY prepare_repo.sh /workspace/prepare_repo.sh
COPY run_prompt.sh /workspace/run_prompt.sh
COPY generate_context.sh /workspace/generate_context.sh
COPY maintain_volume.sh /workspace/maintain_volume.sh
COPY provision_runtimes.sh /workspace/provision_runtimes.sh
COPY entrypoint.sh /entrypoint.sh

# Strip CRLF if a Windows checkout baked \\r into scripts.
RUN find /workspace /entrypoint.sh -type f \( -name '*.sh' -o -path '/entrypoint.sh' \) \
        -exec sed -i 's/\r$//' {} + \
    && chmod +x /entrypoint.sh /workspace/prepare_repo.sh /workspace/run_prompt.sh \
        /workspace/generate_context.sh /workspace/maintain_volume.sh \
        /workspace/provision_runtimes.sh

RUN groupadd -r xianix && useradd -r -g xianix -d /workspace xianix \
    && mkdir -p /workspace/repo /workspace/runtimes \
    && chown -R xianix:xianix /workspace
USER xianix

ENTRYPOINT ["/entrypoint.sh"]
