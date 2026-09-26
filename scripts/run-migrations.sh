#!/usr/bin/env bash
set -euo pipefail

CONNECTION_STRING="${ConnectionStrings__Default:-Host=localhost;Port=5437;Database=notification_db;Username=sellora;Password=sellora_dev_pass}"
export ConnectionStrings__Default="${CONNECTION_STRING}"

dotnet ef database update \
  --project src/Sellora.NotificationService.Infrastructure/Sellora.NotificationService.Infrastructure.csproj \
  --startup-project src/Sellora.NotificationService.Api/Sellora.NotificationService.Api.csproj

echo "Migrations applied successfully"
