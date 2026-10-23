#!/usr/bin/env bash
set -euo pipefail

OWNER="Team-Sellora"
REPO="sellora-notification"
BRANCH="${1:?Usage: $0 <branch-name>}"
TOKEN="${GITHUB_TOKEN:?GITHUB_TOKEN environment variable is required}"

curl -sf -X PUT \
  -H "Accept: application/vnd.github+json" \
  -H "Authorization: Bearer ${TOKEN}" \
  -H "X-GitHub-Api-Version: 2022-11-28" \
  "https://api.github.com/repos/${OWNER}/${REPO}/branches/${BRANCH}/protection" \
  -d '{
    "required_status_checks": {
      "strict": true,
      "contexts": ["build-test-analyze"]
    },
    "enforce_admins": true,
    "required_pull_request_reviews": {
      "dismiss_stale_reviews": true,
      "require_code_owner_reviews": true,
      "required_approving_review_count": 1
    },
    "restrictions": null,
    "allow_force_pushes": false,
    "allow_deletions": false,
    "block_creations": false,
    "required_linear_history": false,
    "required_conversation_resolution": true
  }'

echo ""
echo "Branch protection applied to: ${BRANCH}"
