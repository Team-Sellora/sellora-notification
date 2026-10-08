#!/usr/bin/env bash
set -euo pipefail

# .NET reads the connection string from ConnectionStrings__Default. It is read
# with printenv and passed on with env, so the script itself only uses
# ALL_CAPS shell variables.
DEFAULT_CONNECTION_STRING="Host=localhost;Port=5437;Database=notification_db;Username=sellora;Password=sellora_dev_pass"
CONNECTION_STRING="$(printenv ConnectionStrings__Default || true)"
CONNECTION_STRING="${CONNECTION_STRING:-${DEFAULT_CONNECTION_STRING}}"

env "ConnectionStrings__Default=${CONNECTION_STRING}" dotnet ef database update \
  --project src/Sellora.NotificationService.Infrastructure/Sellora.NotificationService.Infrastructure.csproj \
  --startup-project src/Sellora.NotificationService.Api/Sellora.NotificationService.Api.csproj

echo "Migrations applied successfully"
