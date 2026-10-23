FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["src/Sellora.NotificationService.Api/Sellora.NotificationService.Api.csproj", "src/Sellora.NotificationService.Api/"]
COPY ["src/Sellora.NotificationService.Application/Sellora.NotificationService.Application.csproj", "src/Sellora.NotificationService.Application/"]
COPY ["src/Sellora.NotificationService.Domain/Sellora.NotificationService.Domain.csproj", "src/Sellora.NotificationService.Domain/"]
COPY ["src/Sellora.NotificationService.Infrastructure/Sellora.NotificationService.Infrastructure.csproj", "src/Sellora.NotificationService.Infrastructure/"]

RUN dotnet restore src/Sellora.NotificationService.Api/Sellora.NotificationService.Api.csproj

COPY src/ src/

RUN dotnet publish src/Sellora.NotificationService.Api/Sellora.NotificationService.Api.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish .

# The aspnet image ships a non-root "app" user; 8080 needs no root to bind.
USER $APP_UID

EXPOSE 8080

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "Sellora.NotificationService.Api.dll"]
