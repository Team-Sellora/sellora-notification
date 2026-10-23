# US-E5-1 — Consume order and payment events

## What happens to one Kafka record
```
consume ─▶ NotificationPlanner.Plan (pure)
             ├─ Notify      ─▶ insert NotificationRequest (Pending) + recipients ─▶ commit offset
             │                   └─ event ID already stored ─▶ log "duplicate" ────▶ commit offset
             ├─ Ignore      (known, notifies nobody) ──────────────────────────────▶ commit offset
             └─ DeadLetter  ─▶ produce to dead-letter topic (awaited) ─────────────▶ commit offset
any exception (database down, dead-letter topic down) ─▶ NO commit ─▶ rejoin after RetryDelay ─▶ replay
```

## Topic
The backlog says `order.events`; sellora-order actually publishes to **`sellora.order.v1`** (its `KafkaOptions.OrderTopic`, and what Inventory consumes). This service reads that, with its own consumer group `sellora.notification.order.v1`, so it gets every event independently of Inventory.

## Which events notify whom
| Event | Template | Recipients |
|---|---|---|
| `OrderPlaced` | `order-placed.v1` | shop + agency |
| `OrderConfirmed` | `order-confirmed.v1` | shop + agency |
| `PaymentRecorded` | `payment-recorded.v1` | shop + agency (US-E5-2 sends both the same content) |
| `OrderCancelled` | `order-cancelled.v1` | shop + agency |
| `OrderApproved`, `VanStockReturned` | — | **ignored**: recognised, committed, no request (approval is followed by `OrderConfirmed`, which notifies) |
| anything else | — | **dead-lettered** |

- Shop recipient: `shop.shopId`, `shop.ownerName` (falls back to shop name), `shop.ownerEmail`. Agency: `agency.agencyId`, `agency.name`, `agency.email`.
- **A missing email does not drop the request.** The recipient is stored with `email = null` and a warning is logged; US-E5-3 reports it as unreachable. Dropping it would make a missing address invisible.
- One request per event, holding both recipients (backlog: "exactly one NotificationRequest with the recipients"). Per-recipient send state comes with US-E5-2 so a failure to reach one never resends to the other.

## Exactly one request per event (T3)
- `notification_request.source_event_id` has a **unique index** — the guarantee.
- The intake looks the event ID up first so the normal redelivery case is quiet, and if two consumers race, the loser's insert hits the index (`23505`) and is reported as a duplicate.
- A duplicate is **logged** (`NotificationEventDuplicate … request … already exists`), never silently dropped (Q2).

## Offsets and replay (T4)
- Auto-commit is off. The offset is committed **only after** the record is stored, recognised as a duplicate or ignorable, or acknowledged by the dead-letter topic.
- Crash **before** storing → offset not committed → replayed → stored once.
- Crash **after** storing, **before** committing → replayed → unique index → duplicate → still one row.
- Each record gets a fresh DI scope and `DbContext`, so a failed save leaves nothing behind for the replay.

## Dead-letter topic
`sellora.notification.dead-letter.v1`, same envelope as Inventory's: `sourceTopic`, `sourcePartition`, `sourceOffset`, `messageKey`, **`payload` (the raw value, untouched)**, `headers` (base64), `reason`, `failedAt`; key `topic:partition:offset`; header `dead-letter-reason`.

Dead-lettered: empty payload, invalid JSON, a non-object, missing `eventType`, an unknown event type, `schemaVersion` other than `1.0`, or a notifiable event missing `eventId`, `companyId`, `orderId`, `orderReference`, `shop.shopId`, `agency.agencyId`, `occurredAt` (and, for `PaymentRecorded`, `payment` and `checkInLocation`, which the US-E5-2 message needs).

The dead-letter produce is awaited before committing: if it fails, the record replays instead of being lost. A poison record is committed once parked, so the next record on the partition is processed (Q4).

## First deploy: `AutoOffsetReset = Latest`
Only applies while the consumer group has no committed offset. `Earliest` would turn every historical event on `sellora.order.v1` into a Pending request — and, once US-E5-2 sends, into emails about old orders. `Latest` starts from now; from then on committed offsets decide. Set it to `Earliest` only on purpose (e.g. a fresh environment you want back-filled).

## Read API (company admin)
`GET /api/notification-requests?orderReference=ORD-…` / `?orderId=…` and `GET /api/notification-requests/{id}` — tenant-scoped, CompanyAdmin only (it shows emails). Lets QA check Q1–Q3 without database access.

## Tables
`notification_request` (status check `Pending` only for now; payload `jsonb`; unique `source_event_id`; indexes on company+order and status+received), `notification_recipient` (kind `Shop`/`Agency`, unique per request+kind).
