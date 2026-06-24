# Stripe CRM Bridge

A minimal, open-source ASP.NET Core service that routes Stripe webhook events to HubSpot CRM.

Built and operated by [Beneficial Strategies](https://beneficialstrategies.com).

## What it does

Receives Stripe webhook events and upserts the corresponding customer record in HubSpot.
The source of truth for all contact data is Stripe — no separate database is required.

### Events handled

| Stripe Event | CRM Action |
|---|---|
| `checkout.session.completed` | Create/update contact with tier and status |
| `customer.subscription.updated` | Sync current tier and subscription status |
| `customer.subscription.deleted` | Mark contact as canceled/churned |
| `customer.subscription.trial_will_end` | Set `trial_end_date` — triggers nurture sequence |
| `invoice.payment_succeeded` | Confirm active status on renewal |
| `invoice.payment_failed` | Mark past_due or canceled on final failure |
| `customer.updated` | Sync card expiry for proactive card-expiry emails |

### What is NOT handled here

Business logic that affects application state (user tier changes, database updates,
seat provisioning) is handled by the main application's own Stripe webhook endpoint.
This service is CRM-only and has no write access to any application database.

## Architecture

```
Stripe → POST /stripe/webhook → CrmWebhookProcessor → HubSpotService → HubSpot API
```

Two separate Stripe webhook destinations are registered in the Stripe Dashboard:

1. **Main app webhook** (`/stripe/webhook` on the application server) — updates the database
2. **This service** (`/stripe/webhook` here) — updates the CRM

Each has its own signing secret. A failure here never affects the main application.

## Configuration

Set via environment variables (never in source):

```
Stripe__SecretKey=sk_live_...          # for fetching customer details
Stripe__WebhookSecret=whsec_...        # signing secret for this endpoint
HubSpot__Enabled=true
HubSpot__AccessToken=pat-na1-...
```

## HubSpot custom properties

Create these in HubSpot before enabling (Settings → Properties → Contacts):

| Property name | Type |
|---|---|
| `stripe_customer_id` | Single-line text |
| `internal_customer_id` | Single-line text |
| `subscription_tier` | Single-line text |
| `subscription_status` | Single-line text |
| `trial_start_date` | Date |
| `trial_end_date` | Date |
| `card_exp_month` | Single-line text |
| `card_exp_year` | Single-line text |

## Deployment

Designed for Cloud Run (scales to zero — near-zero cost at low volume):

```bash
docker build -t stripe-crm-bridge .
gcloud run deploy stripe-crm-bridge --image stripe-crm-bridge --region us-central1
```

See [PRIVACY.md](PRIVACY.md) for a full description of what data flows where.
