# Data Flow

## Event routing

Two Stripe webhook destinations are registered in the Stripe Dashboard. Each receives
all events independently; each has its own signing secret.

```
Stripe
├── Destination 1: https://iso20022-mcp.beneficialstrategies.com/stripe/webhook
│   Events: checkout.session.completed, customer.subscription.*, invoice.*
│   Purpose: Update application database (user tiers, subscription status)
│   Code: Iso20022MasterControl / StripeWebhookProcessor.cs
│
└── Destination 2: https://stripe-crm-bridge-260667003376.us-central1.run.app/stripe/webhook
    Events: checkout.session.completed, customer.subscription.*, invoice.*, customer.updated
    Purpose: Update HubSpot CRM contacts
    Code: StripeCrmBridge / CrmWebhookProcessor.cs (this repo)
```

The destination is the **Cloud Run service URL plus the path `/stripe/webhook`**, and nothing else (a destination that ends in
`/webhook` reaches no route and Stripe's deliveries fail with 404: this happened on 2026-11-05). The services:

| Environment | Cloud Run service | Stripe mode | Service URL |
|---|---|---|---|
| Production | `stripe-crm-bridge` | live | `https://stripe-crm-bridge-260667003376.us-central1.run.app` |
| Staging | `stripe-crm-bridge-staging` | test | `https://stripe-crm-bridge-staging-260667003376.us-central1.run.app` |

Cloud Run also answers on a second form of each address (`https://<service>-2qfqooejfq-uc.a.run.app`, the `status.url` the deploy
reads); both reach the same service. The destination registered in the Stripe Dashboard may use either, so compare it with the
table before changing anything. **Renaming a Cloud Run service changes its URL and so the Stripe
destination:** change the destination in the Stripe Dashboard in the same step (StripeCrmBridge#1 and #2). Every deploy checks that
this route answers: it posts a request with a bad signature, one with none and one with an empty one, and each must get 400
(`.github/workflows/deploy.yml`, "Smoke test the webhook route").

## Contact upsert flow

```
Stripe event received
        │
        ▼
Validate signature (whsec_... secret for this endpoint only)
        │
        ▼
CrmWebhookProcessor.ProcessAsync()
        │
        ├── Parse event payload (no DB lookup)
        │
        ├── Fetch Stripe Customer (GET /v1/customers/{id}) if email not in payload
        │
        └── Build ContactSyncData
                │
                ▼
        HubSpotService.SyncContactAsync()
                │
                ▼
        POST /crm/v3/objects/contacts/batch/upsert
        (keyed on email — upsert, never duplicate)
                │
                ▼
        HubSpot contact record updated
```

## Failure behavior

Stripe treats every non-2xx answer as a failed delivery and delivers the event again (live mode: with growing intervals for up to
three days), and shows the status and the body of each attempt on its dashboard. So an event whose work did not land is never
answered 200. The sync is an upsert keyed on the Stripe customer ID, so a delivery that is repeated is safe; a late repeat can apply
older state after a newer event, which the nightly sweep corrects.

- **HubSpot unreachable, timeout (10 seconds), 408, 429 or 5xx:** **503** with a `Retry-After` header (HubSpot's own on a 429, else
  60 seconds) and the body `{"error":"hubspot_sync_failed","retryable":true,"hubspotStatus":...}`. A repeat can succeed as it is.
- **HubSpot refused the request (400, 401, 403, 404 and the like):** **502** with the same body and `"retryable":false`. A repeat
  cannot succeed until someone looks (a revoked token, a property that is not defined, bad data); a delivery after the fix lands it.
  An email that belongs to another contact is not this case: it is recovered (merged, or synced without the email) and logged.
- **Any other failure while handling the event:** 500.
- **Every one of these** is logged at Error with the event ID, the HubSpot status and whether a repeat can cure it
  (`HubSpot sync failed: {EventType} ({EventId}) status=... retryable=...`).
- **HubSpot switched off** (`HubSpot:Enabled` false): nothing is sent, the log says so, and Stripe receives 200.
- **Stripe signature missing or invalid:** 400. Stripe delivers it again, which fails the same way: only a wrong webhook secret
  makes this happen to a real event.
- **Stripe Customer fetch fails:** warning logged; the sync goes on without the email (the contact is keyed on the customer ID).
- **Event type nobody handles:** `ignored` at Information, with the event ID; Stripe receives 200.
- **The nightly sweep** (many contacts at once) does not use these answers: a failure is logged and the sweep carries on.

Every line written while an event is handled carries its ID (`StripeEventId`, `StripeEventType` in the scope), and the logs
hold IDs only: no email address or name.

## Nightly reconciliation

Separately, the main application runs `POST /admin/daily-snapshot` via Cloud Scheduler
at 6am UTC. This reads all customer records from the application database and bulk-syncs
them to HubSpot — correcting any drift from missed or failed webhook events.

The two mechanisms are complementary:
- Webhook bridge: near-real-time, Stripe-event-driven
- Nightly sweep: correctness guarantee, database-authoritative
