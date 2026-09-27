# US-E5-3 — Non-blocking failure handling

## States
| Request status | Meaning | What happens next |
|---|---|---|
| `Pending` | stored, not attempted (or queued by a resend) | the dispatcher picks it up |
| `Sent` | every recipient has it | done |
| `PartiallySent` | some recipients have it | only the rest are retried |
| `Failed` | nobody has it yet | everyone owed it is retried |
| `PermanentlyFailed` | someone cannot be reached: permanent rejection, no address, or 5 failed attempts | **no automatic retries** — on the admin list for a resend |

Per recipient: `Pending → Sent`, `Failed` (retrying), `PermanentlyFailed`, `Unaddressed` (no email). A recipient marked `Sent` is never sent again.

## T1 — Retry with exponential back-off, jitter and a cap
The existing dispatcher (US-E5-2) is the retry worker: every 5 s it claims due `Pending`, `PartiallySent` and `Failed` requests (`FOR UPDATE SKIP LOCKED`).
- Delay after the *n*-th failure = `RetryBaseSeconds × 2^(n−1)`, capped at `RetryMaxSeconds`, then **±20 % jitter** (`JitterRatio`), so everything that failed during an outage doesn't retry in the same second when it ends. Defaults: **30 s, 1, 2, 4 min…** (backlog's suggested defaults).
- **`MaxAttempts` = 5** per recipient. The fifth transient failure moves the recipient to `PermanentlyFailed` ("Gave up after 5 attempts. Last error: …") — the dead-letter point.

## T2 — Transient vs permanent
`SmtpFailureClassifier` follows SMTP's own rule (RFC 5321): **4xx = try later, 5xx = permanent**.

| Failure | Class | Why |
|---|---|---|
| connection refused / timeout / I/O / TLS drop | transient | provider or network is down |
| 4xx (421 busy, 450/451 greylisted, 452 throttled) | transient | the server said "later" |
| 5xx at RCPT (550 user unknown, 553 bad address) | **permanent** | the synchronous hard bounce |
| other 5xx (554 rejected) | **permanent** | refused outright |
| malformed address (`EmailAddressRules`) | **permanent**, *before* connecting | would be rejected every time |
| 5xx sender refused (unverified Brevo sender), 535 auth failed | transient | our configuration, not the recipient; fixing the settings lets retries recover |

The backlog's "5xx → retry" wording comes from HTTP mail APIs, where 5xx means the provider is broken. Over SMTP a 5xx is a refusal, so it must not spend the retry budget. Asynchronous bounces (a mailbox that accepts then bounces later, reported by Brevo webhooks) are out of scope.

## T3 — Orders are never affected
- sellora-order publishes events through its outbox after the order is committed; it never calls this service and never waits for it.
- Here, the Kafka consumer only **stores** requests; sending is a separate hosted service. A failed send is recorded and retried — it never throws into the consumer, never holds an offset, never touches an order.
- Proven by `OrderEventConsumerTests.During_a_full_mail_outage_the_consumer_keeps_storing_every_event`: consumer and dispatcher run together with every send failing; every event is stored and queued as `Failed` with a retry scheduled.

## T4 — Admin list and resend (CompanyAdmin, own company only)
The US-E5-1 resource now also answers at the backlog's path — `/api/notifications` and `/api/notification-requests` are the same controller.
- `GET /api/notifications?status=failed` — Failed, PartiallySent and PermanentlyFailed, newest attempt first. Each row: recipients with address and delivery state, **failure reason**, attempt count, last and next attempt, and the full **attempt history**.
- `POST /api/notifications/{id}/resend` — body optional: `{ "recipients": [{ "kind": "Agency", "email": "orders@agency.lk" }] }` to correct an address. Every recipient not yet sent to is queued with a **fresh retry budget** and the request is dispatched immediately; the response is the request after that attempt. Recipients already sent to are untouched. The resend is a **new attempt** (`trigger = ManualResend`, `triggeredBy = admin's sub`); earlier failures stay in the history.
  - `400` invalid address or kind · `404` not visible · `409` nothing to resend (all sent, or no address anywhere) · `202` another instance is sending it right now (it's queued).

## T5 — Dashboard count
`GET /api/notifications/health` → `{ pending, failed, partiallySent, permanentlyFailed, needsAttention, oldestFailureAt }` in one grouped query. The web dashboard shows `needsAttention` in **Needs attention**, linking to the Failed notifications page.

## D1 — Simulating an outage
- Local: `docker stop notification-mailhog` (start it again to recover), or
- anywhere: `Smtp__SimulateOutage=true` → every send fails transiently with `421 … (simulated)`; set it back to `false` and restart to recover.

## DoD 5 — attempt history
`notification_attempt`: request, recipient, kind, **address used**, attempt number, **timestamp**, **outcome**, **provider response** (e.g. `250 2.0.0 OK queued as …` / `550 5.1.1 user unknown`), error, trigger, triggered by. Append-only.

## Migration `AddFailureHandling`
New table `notification_attempt`; `notification_request.last_resend_at`, `last_resend_by`; `notification_recipient.failures_since_reset` (default 0); status check constraints widened. No data changes.
