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

        await processor.ProcessAsync(stripeEvent);
        return Ok();
    }
}
