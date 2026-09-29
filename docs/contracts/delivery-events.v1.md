# Delivery events — contract v1 (for E6 Delivery & Returns)

Delivery & Returns (E6) isn't built yet. This is the contract sellora-notification consumes for US-E5-4, written down so E6 publishes exactly this.

- **Topic:** `sellora.delivery.v1` (the name Inventory already expects for `ReturnAccepted`)
- **Key:** the order reference (`ORD-…`), so every event for one order stays in order
- **Headers:** `correlation-id`, like the order topic
- **JSON:** camelCase, `schemaVersion` `"1.0"`

## `DeliveryStatusChanged`
| Field | Type | Notes |
|---|---|---|
| `eventId` | uuid | unique; the notification idempotency key |
| `eventType` | string | `DeliveryStatusChanged` |
| `schemaVersion` | string | `1.0` |
| `companyId` | uuid | |
| `deliveryId` | uuid | |
| `orderId`, `orderReference` | uuid, string | the order being delivered |
| `previousStatus`, `status` | string | e.g. `Assigned` → `InTransit` |
| `occurredAt` | timestamp | |
| `correlationId` | string | |
| `reason` | string \| null | why (for `Failed`, `Cancelled`) |
| `scheduledFor` | timestamp \| null | |
| `shop` | `{ shopId, name, ownerName, ownerEmail }` | copy from the order's `OrderPlaced` |
| `agency` | `{ agencyId, name, email }` | copy from the order's `OrderPlaced` |

**Only `InTransit`, `Delivered`, `Failed` and `Cancelled` are emailed.** Publish every transition anyway — other consumers may need them — Notification ignores the internal ones.

## `DeliveryDisputed`
Same envelope and `shop` / `agency`, plus `disputeReason` (string) and `raisedByRole` (e.g. `ShopOwner`). Always emailed to the shop and the agency.

If `ownerEmail` / `email` are omitted, Notification uses the addresses it learned from that shop's and agency's order events.
