using BeneficialStrategies.StripeCrmBridge.HubSpot;
using Stripe;
using StripeSubscription = Stripe.Subscription;
using StripeCheckoutSession = Stripe.Checkout.Session;
using StripeInvoice = Stripe.Invoice;
using StripeCustomer = Stripe.Customer;

namespace BeneficialStrategies.StripeCrmBridge.Controllers;

/// <summary>
/// Maps Stripe events to HubSpot contact upserts. No database access — all contact
/// data is derived from the Stripe event payload and the Stripe Customer API.
/// <para>
/// This processor intentionally handles a superset of events compared to the main
/// application's webhook processor. Events like <c>customer.subscription.trial_will_end</c>
/// and <c>customer.updated</c> have no effect on application state but are valuable
/// CRM signals for lifecycle email sequences.
/// </para>
/// </summary>
public class CrmWebhookProcessor(
    IHubSpotService hubSpot,
    CustomerService customerService,
    ILogger<CrmWebhookProcessor> logger)
{
    public async Task ProcessAsync(Event stripeEvent)
    {
        logger.LogDebug("CRM processing event: {EventType}", stripeEvent.Type);

        switch (stripeEvent.Type)
        {
            case EventTypes.CheckoutSessionCompleted:
                await HandleCheckoutSessionCompleted(stripeEvent);
                break;
            case EventTypes.CustomerSubscriptionUpdated:
                await HandleSubscriptionUpdated(stripeEvent);
                break;
            case EventTypes.CustomerSubscriptionDeleted:
                await HandleSubscriptionDeleted(stripeEvent);
                break;
            case EventTypes.CustomerSubscriptionTrialWillEnd:
                await HandleTrialWillEnd(stripeEvent);
                break;
            case EventTypes.InvoicePaymentSucceeded:
                await HandleInvoicePaymentSucceeded(stripeEvent);
                break;
            case EventTypes.InvoicePaymentFailed:
                await HandleInvoicePaymentFailed(stripeEvent);
                break;
            case EventTypes.CustomerUpdated:
                await HandleCustomerUpdated(stripeEvent);
                break;
            default:
                logger.LogDebug("CRM ignoring event: {EventType}", stripeEvent.Type);
                break;
        }
    }

    // ── Handlers ────────────────────────────────────────────────────────────────

    private async Task HandleCheckoutSessionCompleted(Event stripeEvent)
    {
        var session = stripeEvent.Data.Object as StripeCheckoutSession;
        if (session == null) return;

        // Derive tier from subscription price metadata (same convention as main app)
        var tier = session.Metadata?.GetValueOrDefault("tier") ?? "pro";
        var internalUserId = session.Metadata?.GetValueOrDefault("userId");
        var isTrialing = session.Metadata?.GetValueOrDefault("trial") == "true";
        var billingEmail = session.CustomerEmail ?? await GetCustomerEmailAsync(session.CustomerId);

        var contact = new ContactSyncData(
            StripeCustomerId: session.CustomerId,
            BillingEmail: billingEmail,
            LoginIdentity: null,
            DisplayName: session.CustomerDetails?.Name,
            InternalUserId: internalUserId,
            SubscriptionTier: tier,
            SubscriptionStatus: isTrialing ? "trialing" : "active",
            TrialStartDate: isTrialing ? DateTime.UtcNow : null,
            TrialEndDate: null,
            CardLastFour: null,
            CardExpMonth: null,
            CardExpYear: null);

        await SyncAsync(contact);
    }

    private async Task HandleSubscriptionUpdated(Event stripeEvent)
    {
        var sub = stripeEvent.Data.Object as StripeSubscription;
        if (sub == null) return;

        var contact = new ContactSyncData(
            StripeCustomerId: sub.CustomerId,
            BillingEmail: await GetCustomerEmailAsync(sub.CustomerId),
            LoginIdentity: null,
            DisplayName: null,
            InternalUserId: null,
            SubscriptionTier: GetTierFromSubscription(sub),
            SubscriptionStatus: sub.Status,
            TrialStartDate: sub.TrialStart,
            TrialEndDate: sub.TrialEnd,
            CardLastFour: null,
            CardExpMonth: null,
            CardExpYear: null);

        await SyncAsync(contact);
    }

    private async Task HandleSubscriptionDeleted(Event stripeEvent)
    {
        var sub = stripeEvent.Data.Object as StripeSubscription;
        if (sub == null) return;

        var contact = new ContactSyncData(
            StripeCustomerId: sub.CustomerId,
            BillingEmail: await GetCustomerEmailAsync(sub.CustomerId),
            LoginIdentity: null,
            DisplayName: null,
            InternalUserId: null,
            SubscriptionTier: "free",
            SubscriptionStatus: "canceled",
            TrialStartDate: null,
            TrialEndDate: null,
            CardLastFour: null,
            CardExpMonth: null,
            CardExpYear: null);

        await SyncAsync(contact);
    }

    /// <summary>
    /// Fires 3 days before a trial ends. Sets <c>trial_end_date</c> on the HubSpot
    /// contact so enrollment-based workflows can trigger the nurture sequence.
    /// </summary>
    private async Task HandleTrialWillEnd(Event stripeEvent)
    {
        var sub = stripeEvent.Data.Object as StripeSubscription;
        if (sub == null) return;

        var contact = new ContactSyncData(
            StripeCustomerId: sub.CustomerId,
            BillingEmail: await GetCustomerEmailAsync(sub.CustomerId),
            LoginIdentity: null,
            DisplayName: null,
            InternalUserId: null,
            SubscriptionTier: null,
            SubscriptionStatus: "trial_ending",
            TrialStartDate: null,
            TrialEndDate: sub.TrialEnd,
            CardLastFour: null,
            CardExpMonth: null,
            CardExpYear: null);

        await SyncAsync(contact);
        logger.LogInformation("Trial ending in 3 days: StripeCustomer={CustomerId}, TrialEnd={TrialEnd}",
            sub.CustomerId, sub.TrialEnd);
    }

    private async Task HandleInvoicePaymentSucceeded(Event stripeEvent)
    {
        var invoice = stripeEvent.Data.Object as StripeInvoice;
        if (invoice == null || invoice.BillingReason != "subscription_cycle") return;

        var contact = new ContactSyncData(
            StripeCustomerId: invoice.CustomerId,
            BillingEmail: invoice.CustomerEmail ?? await GetCustomerEmailAsync(invoice.CustomerId),
            LoginIdentity: null,
            DisplayName: null,
            InternalUserId: null,
            SubscriptionTier: null,
            SubscriptionStatus: "active",
            TrialStartDate: null,
            TrialEndDate: null,
            CardLastFour: null,
            CardExpMonth: null,
            CardExpYear: null);

        await SyncAsync(contact);
    }

    private async Task HandleInvoicePaymentFailed(Event stripeEvent)
    {
        var invoice = stripeEvent.Data.Object as StripeInvoice;
        if (invoice == null || invoice.BillingReason == "subscription_create") return;

        var isFinal = invoice.NextPaymentAttempt == null;

        var contact = new ContactSyncData(
            StripeCustomerId: invoice.CustomerId,
            BillingEmail: invoice.CustomerEmail ?? await GetCustomerEmailAsync(invoice.CustomerId),
            LoginIdentity: null,
            DisplayName: null,
            InternalUserId: null,
            SubscriptionTier: null,
            SubscriptionStatus: isFinal ? "canceled" : "past_due",
            TrialStartDate: null,
            TrialEndDate: null,
            CardLastFour: null,
            CardExpMonth: null,
            CardExpYear: null);

        await SyncAsync(contact);
    }

    /// <summary>
    /// Fires when card expiry or other customer data changes. Updates HubSpot with
    /// current card expiry so workflows can trigger proactive card-expiry emails.
    /// </summary>
    private async Task HandleCustomerUpdated(Event stripeEvent)
    {
        var customer = stripeEvent.Data.Object as StripeCustomer;
        if (customer == null) return;

        // DefaultSource is a Card when the customer has a saved card
        var card = customer.DefaultSource as Card
                   ?? (customer.Sources?.Data?.OfType<Card>().FirstOrDefault());

        var contact = new ContactSyncData(
            StripeCustomerId: customer.Id,
            BillingEmail: customer.Email,
            LoginIdentity: null,
            DisplayName: customer.Name,
            InternalUserId: customer.Metadata?.GetValueOrDefault("userId"),
            SubscriptionTier: null,
            SubscriptionStatus: null,
            TrialStartDate: null,
            TrialEndDate: null,
            CardLastFour: card?.Last4,
            CardExpMonth: (int?)card?.ExpMonth,
            CardExpYear: (int?)card?.ExpYear);

        await SyncAsync(contact);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private async Task SyncAsync(ContactSyncData contact)
    {
        try
        {
            await hubSpot.SyncContactAsync(contact);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "HubSpot sync failed for {StripeCustomerId}", contact.StripeCustomerId);
        }
    }

    private async Task<string?> GetCustomerEmailAsync(string customerId)
    {
        try
        {
            var customer = await customerService.GetAsync(customerId);
            return customer?.Email;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not fetch Stripe customer {CustomerId}", customerId);
            return null;
        }
    }

    private static string GetTierFromSubscription(StripeSubscription sub)
    {
        var tier = sub.Items?.Data?.FirstOrDefault()?.Price?.Metadata?.GetValueOrDefault("tier");
        return tier?.ToLowerInvariant() ?? "pro";
    }
}
