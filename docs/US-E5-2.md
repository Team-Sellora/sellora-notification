# US-E5-2 — Simultaneous identical dual dispatch with location

## Flow (every `Dispatch:PollIntervalSeconds`, default 5 s)
```
claim due requests ──▶ render once (if not yet) ──▶ persist rendered message
   (FOR UPDATE SKIP LOCKED,                              │
    120 s lease)                                         ▼
                           Task.WhenAll( send to every recipient still owed it )
                                                         │
                                                         ▼
                 record per-recipient SentAt / error, send gap, status, next retry
```
Due = `Pending` or `PartiallySent`, never attempted or `next_attempt_at` passed, not claimed by another instance. Two deployment slots or scaled-out instances never take the same request.

## Render once, send twice (T1, T4)
- `NotificationRenderer` (Application, pure) builds **one** subject + HTML + plain text from the stored event. It never sees a recipient, so the text is the same for both: it names the shop *and* the agency, and says both received it at the same moment.
- Contents: order reference (also first in the subject: `[ORD-…] Payment recorded — LKR 2,400.00 — Shop`), shop, agency, rep, order type, amount, payment method, paid-at time (Sri Lanka time + exact UTC), item lines with unit prices and totals, and **where the rep was standing**: coordinates (6 dp), distance from the shop, GPS accuracy, check-in time and a Google Maps link to those exact coordinates.
- The rendered subject/HTML/text and a SHA-256 over them are **stored on the request before anything is sent**. A retry sends the stored message; the request is never re-rendered (the aggregate refuses a second rendering).
- Each email carries `X-Sellora-Notification-Id`, `X-Sellora-Order-Reference` and `X-Sellora-Body-SHA256`, so a forwarded copy can be matched to the stored one.
- All event text is HTML-encoded, and CR/LF are stripped from anything that reaches a header.
- Templates also exist for `OrderPlaced`, `OrderConfirmed` and `OrderCancelled` (same layout, no payment section) so every stored request can be delivered.
- Sample: `docs/samples/payment-recorded.sample.html`.

## Concurrent dispatch and measured simultaneity (T2)
- The recipients still owed the message are sent in one `Task.WhenAll`, each over its own SMTP connection (MailKit's client isn't thread-safe).
- Each recipient's `sent_at` is taken when the provider **accepted** that message. `send_gap_ms` = last − first, set once every addressed recipient has it.
- Tolerance: `Dispatch:SimultaneityToleranceMilliseconds`, default **2000 ms**. A larger gap is logged (`NotificationGapOutsideTolerance`) and shown as `withinTolerance: false` by the read API; it never blocks delivery.
- After a partial send and retry, the gap is the real one (e.g. 120 000 ms), not the gap of the first attempt — the record says what actually happened.

## Partial failure (T3)
Per-recipient state: `Pending → Sent | Failed`, or `Unaddressed` (no email known).

| Outcome of an attempt | Request status | Next |
|---|---|---|
| everyone has it | `Sent` | done |
| someone has it, someone failed | `PartiallySent` | retry **only** the failed recipient |
| nobody has it | `Pending` | retry everyone owed it |
| someone has no email | `PartiallySent` / `Pending` | not retried for them; US-E5-3 reports it |

A recipient marked `Sent` is never sent to again and a late failure can't overwrite it. Retries back off 1, 2, 4 … minutes up to 30 (`RetryBaseSeconds`, `RetryMaxSeconds`), with no attempt limit yet — the attempt limit, terminal `Failed` state and admin list are **US-E5-3**.

## Mail provider
The same MailKit SMTP code for every environment; only `Smtp:*` settings differ.

| Environment | Host | Port | TLS | Auth |
|---|---|---|---|---|
| Local | Mailhog (`docker compose up`), UI http://localhost:8025 | 1025 | off | none |
| Tests | Mailpit in Testcontainers | mapped | off | none |
| Staging | **Brevo** `smtp-relay.brevo.com` | 587 | STARTTLS | Brevo SMTP login + SMTP key |

`Smtp:From` must be a sender verified in Brevo, or Brevo rejects every message. Free plan: 300 emails/day.

## Read API (CompanyAdmin)
- `GET /api/notification-requests?orderReference=…` — now includes each recipient's `deliveryStatus`, `sentAt`, `attempts`, `lastError`, and `dispatch { attemptCount, nextAttemptAt, completedAt, sendGapMilliseconds, toleranceMilliseconds, withinTolerance, renderedBodySha256 }`.
- `GET /api/notification-requests/{id}/rendered` — the exact subject, HTML, text and hash that were sent (T4, for disputes).

## Migration `AddDispatchState`
Adds the rendered-message and dispatch columns to `notification_request`, per-recipient delivery columns to `notification_recipient` (existing rows default to `Pending` / 0 attempts), the widened status check constraints, and the `ix_notification_request_dispatch_due` index.

**On first deploy, every request still `Pending` is dispatched** — on staging that means emails for orders placed since US-E5-1 went live. To start clean, run before deploying:
`UPDATE notification_request SET status = 'Sent', completed_at = now() WHERE status = 'Pending';` (staging only).
