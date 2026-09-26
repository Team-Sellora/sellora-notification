#!/usr/bin/env bash
set -euo pipefail

OWNER="Team-Sellora"
REPO="sellora-notification"
TOKEN="${GITHUB_TOKEN:?GITHUB_TOKEN environment variable is required}"

for BRANCH in main dev; do
  echo "Removing branch protection from: ${BRANCH}"

  curl -sf -X DELETE \
    -H "Accept: application/vnd.github+json" \
    -H "Authorization: Bearer ${TOKEN}" \
    -H "X-GitHub-Api-Version: 2022-11-28" \
    "https://api.github.com/repos/${OWNER}/${REPO}/branches/${BRANCH}/protection"

  echo "Branch protection removed from: ${BRANCH}"
  echo ""
done

echo "All branch protections removed temporarily"
