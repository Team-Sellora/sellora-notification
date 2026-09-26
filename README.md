# sellora-notification

Event-driven notification service for Sellora. It consumes the order events
sellora-order publishes on `sellora.order.v1` and turns each notifiable one
into exactly one **notification request** (shop + agency). Sending the email
is US-E5-2; failure handling is US-E5-3.

## Layout
| Project | Holds |
|---|---|
| `Sellora.NotificationService.Domain` | `NotificationRequest`, `NotificationRecipient`, tenancy contracts |
| `Sellora.NotificationService.Application` | the order-event wire contract, `NotificationPlanner` (who gets what — pure), intake/read contracts |
| `Sellora.NotificationService.Infrastructure` | EF Core (PostgreSQL), the Kafka consumer (`OrderEventConsumerService`), idempotent intake, reads |
| `Sellora.NotificationService.Api` | host: JWT, health, the consumer, and a small company-admin read API |

It is a consumer first: most of the work happens in a background service,
not in controllers.

## Run locally
```powershell
docker compose up -d          # notification-db on localhost:5437
# Kafka: start sellora-infra's stack (localhost:9092)
dotnet tool restore
dotnet run --project src/Sellora.NotificationService.Api   # http://localhost:5090/health
```
Migrations run on startup.

## Configuration
| Setting | Default | Notes |
|---|---|---|
| `ConnectionStrings__Default` | local notification_db | |
| `Kafka__BootstrapServers` | `localhost:9092` | Confluent Cloud bootstrap in staging |
| `Kafka__SaslUsername` / `Kafka__SaslPassword` | empty | Confluent API key/secret; empty = local broker |
| `Kafka__OrderTopic` | `sellora.order.v1` | the topic sellora-order publishes to |
| `Kafka__DeadLetterTopic` | `sellora.notification.dead-letter.v1` | |
| `Kafka__ConsumerGroupId` | `sellora.notification.order.v1` | |
| `Kafka__AutoOffsetReset` | `Latest` | only for a brand-new group; see docs/US-E5-1.md |
| `Jwt__*` | as other services | |

## Tests
`dotnet test` — Docker must be running (PostgreSQL and Kafka run in Testcontainers).

## Docs
- `docs/US-E5-1.md` — consumption, idempotency, dead-lettering, recipient rules.
