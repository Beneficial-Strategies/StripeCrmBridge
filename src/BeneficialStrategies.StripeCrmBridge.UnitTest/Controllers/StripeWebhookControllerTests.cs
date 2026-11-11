using System.Text;
using BeneficialStrategies.StripeCrmBridge.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeneficialStrategies.StripeCrmBridge.UnitTest.Controllers;

/// <summary>A webhook request that Stripe did not sign is refused with 400 before anything else is done: a missing header used to end in a 500 (Iso20022MasterControl#66).</summary>
public class StripeWebhookControllerTests
{
    private static StripeWebhookController ControllerFor(string body, string? signature)
    {
        var http = new DefaultHttpContext();
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        if (signature is not null) http.Request.Headers["Stripe-Signature"] = signature;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Stripe:WebhookSecret"] = "whsec_test" }).Build();
        return new StripeWebhookController(null!, config, NullLogger<StripeWebhookController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task NoSignatureHeader_IsRefusedWith400(string? signature)
    {
        var result = await ControllerFor("{}", signature).HandleWebhook();

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task BadSignature_IsRefusedWith400()
    {
        var result = await ControllerFor("{}", "t=1,v1=not-a-signature").HandleWebhook();

        Assert.IsType<BadRequestObjectResult>(result);
    }
}
