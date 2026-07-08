FROM python:3.12-slim-bookworm

# Pinned to bookworm (Debian 12) on purpose:
#   * Azure CLI ships a bookworm repo (no trixie dist yet).
#   * Avoids Debian trixie's `t64` (64-bit time_t) package-rename transition,
#     which breaks `playwright install --with-deps` on restricted apt mirrors.
# System deps + Node 20 (npx/MCP) + git + GitHub CLI (gh) + Azure CLI + azure-devops extension (az devops).
RUN apt-get update && apt-get install -y --no-install-recommends \
    git \
    curl \
    jq \
    universal-ctags \
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

# Install global Node CLIs: Claude Code + Playwright (lib + CLI driver).
#   PLAYWRIGHT_BROWSERS_PATH      — shared, non-home browser cache so the
#                                   non-root xianix user can find browsers
#                                   at runtime (default is ~/.cache).
#   PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1 — at *runtime* (after USER xianix),
#                                   prevents tools from trying to re-download
#                                   browsers into the read-only /ms-playwright.
#   NODE_PATH=/usr/lib/node_modules — lets `require('playwright')` resolve from
#                                   any CWD; the web-app-tester skill probes
#                                   the cache and preflights the launch via
#                                   `node -e "require('playwright')..."` from
#                                   /workspace, which otherwise hits
#                                   MODULE_NOT_FOUND for global packages.
#   @playwright/cli is the playwright-cli driver the skill shells out to and
#   is intentionally distinct from the `playwright` Node library — both are
#   installed globally so the non-root user never tries `npm install -g` at
#   runtime (which would EACCES on /usr/lib/node_modules).
#   playwright is pinned to match the version the skill uses as a fallback.
# `playwright install --with-deps` runs apt-get internally to pull Chromium's
# system libs, so we clean apt lists again afterwards to keep the image lean.
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright \
    PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1 \
    NODE_PATH=/usr/lib/node_modules
# Pin the global Node CLIs so image behaviour only changes on a deliberate bump.
# Leaving @anthropic-ai/claude-code unpinned meant every rebuild silently pulled
# whatever CLI was latest, so plugin/version resolution could drift with no code
# change. Override at build time with --build-arg CLAUDE_CODE_VERSION=x.y.z.
ARG CLAUDE_CODE_VERSION=2.1.201
ARG PLAYWRIGHT_CLI_VERSION=0.1.15
ARG PLAYWRIGHT_VERSION=1.49.0
RUN npm install -g \
        "@anthropic-ai/claude-code@${CLAUDE_CODE_VERSION}" \
        "@playwright/cli@${PLAYWRIGHT_CLI_VERSION}" \
        "playwright@${PLAYWRIGHT_VERSION}" \
    && PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=0 playwright install --with-deps chromium \
    && chmod -R a+rX /ms-playwright \
    && rm -rf /var/lib/apt/lists/* /root/.npm

WORKDIR /workspace

COPY requirements.txt /workspace/requirements.txt
RUN pip install --no-cache-dir -r /workspace/requirements.txt

# ── Headroom compression proxy (Option B, per-container, opt-in via XIANIX-COMPRESSION) ──
# Only the `[proxy]` extra is installed — NOT `[all]` or `[llmlingua]`, which would pull
# ~2 GB of torch/transformers and add 10–30 s cold start; the wrong trade for our short-lived
# containers. `build-essential` is only needed at install time for a small C++ extension
# (hnswlib) and is removed immediately after to keep the image lean. Override the pinned
# version at build time with --build-arg HEADROOM_VERSION=x.y.z.
ARG HEADROOM_VERSION=0.7.0
RUN apt-get update && apt-get install -y --no-install-recommends build-essential \
    && pip install --no-cache-dir "headroom-ai[proxy]==${HEADROOM_VERSION}" \
    && apt-get purge -y build-essential && apt-get autoremove -y \
    && rm -rf /var/lib/apt/lists/*

# pentest-agent v3.0+ tools (web-application pentesting).
# - semgrep: primary SAST scanner used by sast-scanner.
# - bandit: Python SAST fallback.
# - pip-audit: Python dependency CVE check used by dependency-scanner.
# - trivy: web-ecosystem CVE scanning (npm/pip/maven/gem/composer/go.mod).
# - gitleaks: secrets detection used by secrets-scanner. Installed from a pinned
#   release tarball — gitleaks ships no install.sh, and the old
#   `curl .../install.sh | sh` 404'd silently and left the binary missing.
# The trailing `command -v` checks fail the build loudly if any tool silently
# failed to install (guards against the curl|sh no-op trap).
# Added together so the layer is cached as a unit; ~200 MB combined.
RUN pip install --no-cache-dir semgrep bandit pip-audit \
    && curl -sfL https://raw.githubusercontent.com/aquasecurity/trivy/main/contrib/install.sh \
        | sh -s -- -b /usr/local/bin \
    && curl -sfL https://github.com/gitleaks/gitleaks/releases/download/v8.28.0/gitleaks_8.28.0_linux_x64.tar.gz \
        | tar -xz -C /usr/local/bin gitleaks \
    && chmod +x /usr/local/bin/gitleaks \
    && command -v semgrep && command -v bandit && command -v pip-audit \
    && command -v trivy && command -v gitleaks

# Optional infra-scanner v1.0+ tools, gated by INSTALL_INFRA_TOOLS.
# Pass --build-arg INSTALL_INFRA_TOOLS=1 to include nmap + hadolint + kubesec + syft
# in the image. Skipped by default to keep the image slim for pentest-only deployments.
# (tfsec/checkov are not bundled — install per-environment if needed.)
# kubesec is installed from a pinned release tarball — it ships no install.sh, and
# the old `curl .../install.sh | sh` 404'd silently and left the binary missing.
# The trailing `command -v` checks fail the build loudly if any tool is missing.
ARG INSTALL_INFRA_TOOLS=0
RUN if [ "$INSTALL_INFRA_TOOLS" = "1" ]; then \
        apt-get update && apt-get install -y --no-install-recommends nmap \
        && rm -rf /var/lib/apt/lists/* \
        && curl -sfL https://github.com/hadolint/hadolint/releases/latest/download/hadolint-Linux-x86_64 \
            -o /usr/local/bin/hadolint && chmod +x /usr/local/bin/hadolint \
        && curl -sfL https://github.com/controlplaneio/kubesec/releases/download/v2.14.0/kubesec_linux_amd64.tar.gz \
            | tar -xz -C /usr/local/bin kubesec \
        && chmod +x /usr/local/bin/kubesec \
        && curl -sfL https://raw.githubusercontent.com/anchore/syft/main/install.sh \
            | sh -s -- -b /usr/local/bin \
        && command -v nmap && command -v hadolint && command -v kubesec && command -v syft ; \
    fi

COPY execute_plugin.py /workspace/execute_plugin.py
COPY _common.sh /workspace/_common.sh
COPY prepare_repo.sh /workspace/prepare_repo.sh
COPY run_prompt.sh /workspace/run_prompt.sh
COPY generate_context.sh /workspace/generate_context.sh
COPY maintain_volume.sh /workspace/maintain_volume.sh
COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh /workspace/prepare_repo.sh /workspace/run_prompt.sh \
        /workspace/generate_context.sh /workspace/maintain_volume.sh

RUN groupadd -r xianix && useradd -r -g xianix -d /workspace xianix \
    && mkdir -p /workspace/repo \
    && chown -R xianix:xianix /workspace
USER xianix

ENTRYPOINT ["/entrypoint.sh"]
