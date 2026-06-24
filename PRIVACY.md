# Privacy & Data Handling

This document describes exactly what data this service collects, processes, and transmits.
The implementation is open source — you can verify these claims by reading the code.

## Data flow

```
Stripe (source) → this service → HubSpot (destination)
```

This service acts as a read-only relay. It receives data from Stripe and forwards a
subset of it to HubSpot. It does not store data persistently, write to any database,
or transmit data to any other destination.

## Data received from Stripe

Stripe delivers signed webhook event payloads to this service. Each payload may contain:

- Customer email address
- Customer display name
- Stripe customer ID
- Subscription tier and status
- Subscription trial start and end dates
- Payment card expiry month and year (no card numbers — Stripe never exposes those)
- Internal user ID (stored in Stripe customer metadata by the main application)

## Data forwarded to HubSpot

The following fields are upserted to the HubSpot Contacts API, keyed on `stripe_customer_id`
(not email — the Stripe Customer ID is the stable, immutable identifier):

| HubSpot field | Source | Purpose |
|---|---|---|
| `stripe_customer_id` | Stripe Customer ID | **Contact identity key** — immutable |
| `email` | Stripe Customer email | Correspondence address for outbound sequences |
| `firstname` | Stripe Customer name | Display in CRM |
| `internal_customer_id` | Stripe Customer metadata | Link CRM record to app DB |
| `subscription_tier` | Stripe Price metadata | Segment by plan |
| `subscription_status` | Stripe Subscription status | Lifecycle stage |
| `trial_start_date` | Stripe Subscription | Trial lifecycle |
| `trial_end_date` | Stripe Subscription | Trial expiry / nurture trigger |
| `card_exp_month` | Stripe Card | Card expiry email |
| `card_exp_year` | Stripe Card | Card expiry email |

The `login_identity` field (OAuth login email/username) is populated by the application's
nightly sweep, not by this service, because this service has no access to the application
database.

## Data NOT forwarded

- Payment card numbers or CVV (Stripe never provides these)
- Billing address
- Individual transaction amounts or invoice line items
- Application usage data (queries made, tools used, session content)
- Any data not listed above

## Data NOT stored by this service

This service holds no persistent state. No database, no cache, no logs containing
customer data beyond what Serilog writes to Cloud Logging (customer IDs and event
types only — no email addresses in log output).

## Third-party services

| Service | Purpose | Privacy policy |
|---|---|---|
| Stripe | Billing events source | https://stripe.com/privacy |
| HubSpot | CRM destination | https://legal.hubspot.com/privacy-policy |
| Google Cloud Run | Hosting | https://cloud.google.com/terms/cloud-privacy-notice |

## Contact

Questions about data handling: support@beneficialstrategies.com
