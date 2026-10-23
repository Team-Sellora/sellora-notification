# US-E5-4 — Delivery and low-stock notifications

## Where the events come from
| Event | Producer | Topic | Status |
|---|---|---|---|
| `LowStockDetected` | sellora-inventory (US-E3-5) | `sellora.inventory.v1` | **live** — now also says who holds the stock (`OwnerType`, `ExternalOwnerId`, `OwnerDisplayName`, additive) |
| `DeliveryStatusChanged`, `DeliveryDisputed` | Delivery & Returns (E6) | `sellora.delivery.v1` | **not built yet** — consumer ready against `docs/contracts/delivery-events.v1.md` |

## T1 — Same consumer, more topics
No new consumer: `OrderEventConsumerService` subscribes to `OrderTopic`, `InventoryTopic` and `DeliveryTopic` in the same group, with the same commit-after-persist, idempotency (unique event ID) and dead-letter behaviour.
- **Shared topics are lenient:** the inventory and delivery topics carry events for other consumers, so an unknown event type there is **ignored**. On the order topic it's still dead-lettered.
- **A missing topic doesn't stop anything:** until E6 creates `sellora.delivery.v1`, the consumer logs a warning once a minute and keeps consuming the order and inventory topics (tested with a real broker). Same for a topic it has no READ permission on.
- Inventory serialises its events PascalCase (`EventType`); the planner reads the event type case-insensitively.

## T2 — Who is told
| Event | Recipients | Template |
|---|---|---|
| `DeliveryStatusChanged` → `InTransit`, `Delivered`, `Failed`, `Cancelled` | shop + agency | `delivery-status.v1` |
| `DeliveryStatusChanged` → any other status (`Pending`, `Assigned`, `Scheduled`, `PickedUp`, …) | nobody (ignored; Delivery still records it) | — |
| `DeliveryDisputed` | shop + agency | `delivery-disputed.v1` |
| `LowStockDetected` for **agency** stock | that agency + **company admin** | `low-stock.v1` |
| `LowStockDetected` for **van** or **company** stock | **company admin** only | `low-stock.v1` |

**Never the shop owner for low stock.**

### Where the addresses come from (no calls to other services)
- **Delivery:** from the event (`shop.ownerEmail`, `agency.email`); if omitted, from the directory below.
- **Agency (low stock):** Inventory knows the agency ID, not its email. Notification keeps a small **directory** (`notification_directory`) learned from the order events it already consumes — every order carries its agency's and shop's name and email and its products' names. Latest non-empty value wins. So an agency that has taken at least one order since this deploy is reachable.
- **Company admin:** a company admin sets a **company alert address** on the Failed notifications page (`GET/PUT /api/notifications/settings`, CompanyAdmin only). If it isn't set, the company-admin copy is stored *Unaddressed* and appears on the admin list — visible, not silently skipped.
- **Product name (low stock):** from the directory (learned from order lines); an unseen product shows its ID.

## T3 — Same pipeline
New templates in the same `NotificationRenderer`, rendered once and stored before sending, dispatched by the same `NotificationDispatcher` (concurrent sends, per-recipient state, back-off, 5-attempt cap, admin resend). Nothing new on the sending side. Names looked up at intake are stored in `notification_request.context`; the raw event stays untouched in `payload`.

Low-stock notifications have no order: `order_id` is null and the reference shown is `STOCK-yyMMdd-XXXXXX`.

## Migration `AddDeliveryAndLowStock`
`notification_request.order_id` becomes nullable; new `context jsonb`; recipient kind adds `CompanyAdmin`; new tables `notification_directory` and `notification_settings`.

## Topics (for the shared topic list — US-E5-4-D1)
| Topic | Producer | Consumers |
|---|---|---|
| `sellora.order.v1` | order | inventory, notification |
| `sellora.inventory.v1` | inventory | notification |
| `sellora.delivery.v1` | delivery (E6) | inventory, notification |
| `sellora.hierarchy.v1` | organization | inventory |
| `sellora.notification.dead-letter.v1` | notification | — (inspect manually) |
