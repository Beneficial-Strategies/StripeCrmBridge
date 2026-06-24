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
└── Destination 2: https://stripe-crm-bridge-<hash>.run.app/stripe/webhook
    Events: checkout.session.completed, customer.subscription.*, invoice.*, customer.updated
    Purpose: Update HubSpot CRM contacts
    Code: StripeCrmBridge / CrmWebhookProcessor.cs (this repo)
```

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

- **HubSpot unreachable:** 10-second timeout, exception caught and logged. Stripe receives
  200 OK. The contact will be corrected on the next event or the nightly sweep.
- **Stripe signature invalid:** 400 returned. Stripe will not retry invalid-signature responses.
- **Stripe Customer fetch fails:** Event is skipped with a warning log. No retry.
- **HubSpot non-2xx response:** Logged as warning. Stripe receives 200 OK (no retry triggered).

## Nightly reconciliation

Separately, the main application runs `POST /admin/daily-snapshot` via Cloud Scheduler
at 6am UTC. This reads all customer records from the application database and bulk-syncs
them to HubSpot — correcting any drift from missed or failed webhook events.

The two mechanisms are complementary:
- Webhook bridge: near-real-time, Stripe-event-driven
- Nightly sweep: correctness guarantee, database-authoritative
