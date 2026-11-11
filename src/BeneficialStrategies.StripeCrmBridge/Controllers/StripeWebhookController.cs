using BeneficialStrategies.StripeCrmBridge.HubSpot;
using Microsoft.AspNetCore.Mvc;
using Stripe;

namespace BeneficialStrategies.StripeCrmBridge.Controllers;

/// <summary>
/// Receives Stripe webhook events and routes them to the CRM processor.
/// Register this endpoint as a separate Stripe webhook destination from your
/// main application's webhook — it can subscribe to a different event set.
/// </summary>
[ApiController]
public class StripeWebhookController(
    CrmWebhookProcessor processor,
    IConfiguration config,
    ILogger<StripeWebhookController> logger) : ControllerBase
{
    [HttpPost("/stripe/webhook")]
    public async Task<IActionResult> HandleWebhook()
    {
        // A request without a signature is refused before anything else: the Stripe library throws ArgumentNullException, not StripeException, for a missing one.
        var signature = Request.Headers["Stripe-Signature"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(signature))
        {
            logger.LogWarning("Stripe webhook refused: no Stripe-Signature header");
            return BadRequest("Invalid signature");
        }

        var json = await new StreamReader(Request.Body).ReadToEndAsync();
        var secret = config["Stripe:WebhookSecret"];

        if (string.IsNullOrEmpty(secret))
        {
            logger.LogError("Stripe:WebhookSecret is not configured");
            return StatusCode(500);
        }

        Event stripeEvent;
        try
        {
            stripeEvent = EventUtility.ConstructEvent(json, signature, secret);
        }
        catch (StripeException ex)
        {
            logger.LogWarning("Stripe webhook signature validation failed: {Message}", ex.Message);
            return BadRequest("Invalid signature");
        }

        // Every handled event can be traced by its ID (IDs only: no email addresses or names). The scope puts the ID on every line
        // the processor writes while it handles the event, including its warnings.
        logger.LogInformation("Stripe webhook received: {EventType} ({EventId}) livemode={Livemode} created={Created:o}",
            stripeEvent.Type, stripeEvent.Id, stripeEvent.Livemode, stripeEvent.Created);
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["StripeEventId"] = stripeEvent.Id, ["StripeEventType"] = stripeEvent.Type });
        try
        {
            await processor.ProcessAsync(stripeEvent);
        }
        catch (HubSpotSyncException ex)
        {
            // Never 200 for a sync that did not land: Stripe delivers the event again (live mode: for up to three days). 503 for a failure
            // a repeat can cure, 502 for one HubSpot refused; the body shows in Stripe's dashboard, so the cause is recognizable there.
            logger.LogError(ex, "HubSpot sync failed: {EventType} ({EventId}) status={HubSpotStatus} retryable={Retryable}",
                stripeEvent.Type, stripeEvent.Id, ex.StatusCode, ex.Retryable);
            if (ex.Retryable)
                Response.Headers.RetryAfter = ((int)Math.Clamp((ex.RetryAfter ?? TimeSpan.FromSeconds(60)).TotalSeconds, 1, 3600)).ToString();
            return new ObjectResult(new { error = "hubspot_sync_failed", retryable = ex.Retryable, hubspotStatus = ex.StatusCode })
            {
                StatusCode = ex.Retryable ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status502BadGateway
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Stripe webhook failed: {EventType} ({EventId})", stripeEvent.Type, stripeEvent.Id);
            throw;
        }
        return Ok();
    }
}
