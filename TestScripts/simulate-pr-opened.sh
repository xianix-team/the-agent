#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# simulate-pr-opened.sh
#
# Sends a simulated GitHub "pull_request" webhook to the local Xianix agent
# endpoint, matching the "pull requests / action==opened" rule in rules.json.
#
# Usage:
#   ./simulate-pr-opened.sh <WEBHOOK_URL> [PR_NUMBER] [REPO_URL]
#
# Arguments:
#   WEBHOOK_URL  - (required) full webhook URL including query params
#   PR_NUMBER    - pull request number to simulate (default: 1)
#   REPO_URL     - repository HTML URL (default: https://github.com/example-org/example-repo)
#
# Examples:
#   ./simulate-pr-opened.sh 'http://localhost:5005/api/user/webhooks/builtin?apikeyId=...'
#   ./simulate-pr-opened.sh 'http://...' 42
#   ./simulate-pr-opened.sh 'http://...' 385 https://github.com/XiansAiPlatform/XiansAi.Server
#   WEBHOOK_URL='http://...' PR_NUMBER=385 ./simulate-pr-opened.sh
#
# NOTE: Always quote the WEBHOOK_URL when passing it as an argument to avoid
#       the shell interpreting '&' as a background job operator.
# ─────────────────────────────────────────────────────────────────────────────
set -euo pipefail

WEBHOOK_URL="${1:-${WEBHOOK_URL:-}}"
PR_NUMBER="${2:-${PR_NUMBER:-1}}"
REPO_URL="${3:-${REPO_URL:-https://github.com/example-org/example-repo}}"

if [ -z "${WEBHOOK_URL}" ]; then
  echo "ERROR: WEBHOOK_URL is required."
  echo "Usage: ./simulate-pr-opened.sh '<webhook-url>' [pr-number] [repo-url]"
  echo "  or:  WEBHOOK_URL='<webhook-url>' PR_NUMBER=385 ./simulate-pr-opened.sh"
  exit 1
fi

# Derive repo name and full_name from the URL
# e.g. https://github.com/XiansAiPlatform/XiansAi.Server → XiansAiPlatform/XiansAi.Server
REPO_FULL_NAME=$(echo "${REPO_URL}" | sed 's|https://github.com/||' | sed 's|/$||')
REPO_NAME=$(echo "${REPO_FULL_NAME}" | cut -d'/' -f2)

# Fetch the real PR title and head branch from GitHub if gh is available
PR_TITLE="Simulated PR #${PR_NUMBER}"
PR_HEAD_REF="feature/test-branch"
if command -v gh &>/dev/null && [[ "${REPO_FULL_NAME}" != *"example"* ]]; then
  REAL_TITLE=$(gh pr view "${PR_NUMBER}" --repo "${REPO_FULL_NAME}" --json title -q .title 2>/dev/null || true)
  REAL_REF=$(gh pr view "${PR_NUMBER}" --repo "${REPO_FULL_NAME}" --json headRefName -q .headRefName 2>/dev/null || true)
  [ -n "${REAL_TITLE}" ] && PR_TITLE="${REAL_TITLE}"
  [ -n "${REAL_REF}" ]   && PR_HEAD_REF="${REAL_REF}"
fi

PAYLOAD=$(cat <<EOF
{
  "action": "opened",
  "number": ${PR_NUMBER},
  "pull_request": {
    "number": ${PR_NUMBER},
    "title": "${PR_TITLE}",
    "state": "open",
    "html_url": "${REPO_URL}/pull/${PR_NUMBER}",
    "head": {
      "ref": "${PR_HEAD_REF}",
      "sha": "abc1234567890abcdef1234567890abcdef123456"
    },
    "base": {
      "ref": "main",
      "sha": "def0987654321fedcba0987654321fedcba098765"
    },
    "user": {
      "login": "test-user"
    }
  },
  "repository": {
    "id": 123456789,
    "name": "${REPO_NAME}",
    "full_name": "${REPO_FULL_NAME}",
    "url": "${REPO_URL}",
    "clone_url": "${REPO_URL}.git",
    "html_url": "${REPO_URL}"
  },
  "sender": {
    "login": "test-user"
  }
}
EOF
)

echo "──────────────────────────────────────────────"
echo "  Sending simulated GitHub PR opened webhook"
echo "──────────────────────────────────────────────"
echo "  Endpoint:   ${WEBHOOK_URL}"
echo "  PR number:  ${PR_NUMBER}"
echo "  PR title:   ${PR_TITLE}"
echo "  Head ref:   ${PR_HEAD_REF}"
echo "  Repo:       ${REPO_FULL_NAME}"
echo "──────────────────────────────────────────────"
echo ""

curl -s -X POST "${WEBHOOK_URL}" \
  -H "Content-Type: application/json" \
  -H "X-GitHub-Event: pull_request" \
  -H "X-GitHub-Delivery: $(uuidgen 2>/dev/null || cat /proc/sys/kernel/random/uuid)" \
  -d "${PAYLOAD}" \
  | jq . 2>/dev/null || cat

echo ""
echo "Done."
