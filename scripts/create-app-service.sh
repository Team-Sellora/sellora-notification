#!/usr/bin/env bash
set -euo pipefail

RESOURCE_GROUP="sellora-rg"
LOCATION="australiaeast"
APP_SERVICE_PLAN="sellora-plan"
APP_NAME="sellora-notification"
RUNTIME="DOTNETCORE:8.0"

az webapp create \
  --resource-group "${RESOURCE_GROUP}" \
  --plan "${APP_SERVICE_PLAN}" \
  --name "${APP_NAME}" \
  --runtime "${RUNTIME}"

az webapp deployment slot create \
  --resource-group "${RESOURCE_GROUP}" \
  --name "${APP_NAME}" \
  --slot staging \
  --configuration-source "${APP_NAME}"

az resource update \
  --resource-group "${RESOURCE_GROUP}" \
  --name scm \
  --namespace Microsoft.Web \
  --resource-type basicPublishingCredentialsPolicies \
  --parent "sites/${APP_NAME}" \
  --set properties.allow=true

az resource update \
  --resource-group "${RESOURCE_GROUP}" \
  --name scm \
  --namespace Microsoft.Web \
  --resource-type basicPublishingCredentialsPolicies \
  --parent "sites/${APP_NAME}/slots/staging" \
  --set properties.allow=true

az webapp log config \
  --resource-group "${RESOURCE_GROUP}" \
  --name "${APP_NAME}" \
  --application-logging filesystem \
  --level information \
  --web-server-logging filesystem

az webapp log config \
  --resource-group "${RESOURCE_GROUP}" \
  --name "${APP_NAME}" \
  --slot staging \
  --application-logging filesystem \
  --level information \
  --web-server-logging filesystem

echo "App Service '${APP_NAME}' with staging slot created successfully"
