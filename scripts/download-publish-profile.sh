#!/usr/bin/env bash
set -euo pipefail

RESOURCE_GROUP="sellora-rg"
APP_NAME="sellora-notification"

az webapp deployment list-publishing-profiles \
  --resource-group "${RESOURCE_GROUP}" \
  --name "${APP_NAME}" \
  --slot staging \
  --xml > staging-publish-profile.xml

echo "Publish profile saved to staging-publish-profile.xml"
echo "Add the contents as GitHub secret: AZURE_PUBLISH_PROFILE_STAGING"
