using System.Net;
using System.Text;
using BeneficialStrategies.StripeCrmBridge.HubSpot;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BeneficialStrategies.StripeCrmBridge.UnitTest.HubSpot;

/// <summary>
/// Tests for <see cref="HubSpotService"/>'s email-collision recovery
/// (<c>TryResolveEmailCollisionAsync</c>) — ported from the main MCP server repo's equivalent
/// tests. HubSpot enforces email uniqueness portal-wide, and staging/production share one
/// portal, so a sync can collide with a different existing contact that already owns the
/// address. The <see cref="SyncContactAsync_On400EmailCollision_CollidingContactHasDifferentStripeCustomerId_RefusesMergeAndFallsBackToSyncingWithoutEmail"/>
/// case is the load-bearing safety guardrail: it must never combine two real customers'
/// billing history.
/// </summary>
public class HubSpotServiceTests
{
    private static HubSpotService CreateSut(FakeHttpMessageHandler handler, HubSpotSettings? settings = null)
    {
        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient(handler));

        return new HubSpotService(
            factoryMock.Object,
            Options.Create(settings ?? new HubSpotSettings { Enabled = true, AccessToken = "test-token" }),
            NullLogger<HubSpotService>.Instance);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static InvalidOperationException Unexpected(HttpRequestMessage req) =>
        new($"Unexpected HTTP call: {req.Method} {req.RequestUri}");

    private static ContactSyncData MakeContact(string stripeCustomerId, string email = "user@example.com") =>
        new(
            StripeCustomerId: stripeCustomerId,
            BillingEmail: email,
            LoginIdentity: null,
            DisplayName: "Test User",
            InternalUserId: "user-123",
            SubscriptionTier: "pro",
            SubscriptionStatus: "active",
            TrialStartDate: null,
            TrialEndDate: null,
            CardLastFour: null,
            CardExpMonth: null,
            CardExpYear: null);

    [Fact]
    public async Task SyncContactAsync_Success_PostsSingleUpsertRequestOnly()
    {
        var calls = 0;
        var handler = new FakeHttpMessageHandler(req =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/contacts/batch/upsert"))
            {
                calls++;
                return Json(HttpStatusCode.OK, "{}");
            }
            throw Unexpected(req);
        });

        var sut = CreateSut(handler);

        await sut.SyncContactAsync(MakeContact("cus_123"));

        Assert.Equal(1, calls);
    }

    // -------------------------------------------------------------------------
    // Email collision recovery (TryResolveEmailCollisionAsync)
    // -------------------------------------------------------------------------

    private static string EmailCollisionBody(string thisContactId, string otherContactId) =>
        $$"""
        {"status":"error","message":"Cannot set PropertyValueCoordinates{portalId=1, objectTypeId=ObjectTypeId{legacyObjectType=CONTACT}, propertyName=email, value=x@y.com} on {{thisContactId}}. {{otherContactId}} already has that value.","category":"VALIDATION_ERROR"}
        """;

    [Fact]
    public async Task SyncContactAsync_On400EmailCollision_CollidingContactIsBareLead_MergesAndRetriesWithEmail()
    {
        var upsertCalls = 0;
        var merged = false;
        var handler = new FakeHttpMessageHandler(req =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/contacts/batch/upsert"))
            {
                upsertCalls++;
                return upsertCalls == 1
                    ? Json(HttpStatusCode.BadRequest, EmailCollisionBody("111", "222"))
                    : Json(HttpStatusCode.OK, "{}"); // retry after merge succeeds
            }
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/contacts/cus_new"))
                return Json(HttpStatusCode.OK, """{"id":"111"}"""); // this contact's own HubSpot id
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/contacts/222"))
                return Json(HttpStatusCode.OK, """{"properties":{}}"""); // bare lead, no stripe_customer_id
            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/contacts/merge"))
            {
                merged = true;
                return Json(HttpStatusCode.OK, """{"id":"111"}""");
            }
            throw Unexpected(req);
        });

        var sut = CreateSut(handler);

        await sut.SyncContactAsync(MakeContact("cus_new"));

        Assert.True(merged, "A colliding contact with no stripe_customer_id (a bare lead) should be merged in.");
        Assert.Equal(2, upsertCalls); // original attempt (with email) + retry (with email) after merge
    }

    [Fact]
    public async Task SyncContactAsync_On400EmailCollision_CollidingContactHasDifferentStripeCustomerId_RefusesMergeAndFallsBackToSyncingWithoutEmail()
    {
        var merged = false;
        var withoutEmailAttempted = false;
        var handler = new FakeHttpMessageHandler(req =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/contacts/batch/upsert"))
            {
                var body = req.Content!.ReadAsStringAsync().Result;
                if (body.Contains("\"email\""))
                    return Json(HttpStatusCode.BadRequest, EmailCollisionBody("111", "222"));
                withoutEmailAttempted = true;
                return Json(HttpStatusCode.OK, "{}");
            }
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/contacts/cus_new"))
                return Json(HttpStatusCode.OK, """{"id":"111"}""");
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/contacts/222"))
                return Json(HttpStatusCode.OK, """{"properties":{"stripe_customer_id":"cus_someone_real"}}""");
            if (req.RequestUri!.AbsolutePath.EndsWith("/contacts/merge"))
            {
                merged = true;
                return Json(HttpStatusCode.OK, "{}");
            }
            throw Unexpected(req);
        });

        var sut = CreateSut(handler);

        // A real production customer's email reused in a test signup (cus_new) must never
        // absorb that customer's contact — same guardrail as the main app's HubSpotService.
        await sut.SyncContactAsync(MakeContact("cus_new"));

        Assert.False(merged, "Must never merge a colliding contact that already has a different, real stripe_customer_id.");
        Assert.True(withoutEmailAttempted, "Should still sync tier/status/etc. without the email field after refusing to merge.");
    }

    [Fact]
    public async Task SyncContactAsync_On400EmailCollision_MessageDoesNotParse_FallsBackToSyncingWithoutEmail()
    {
        var withoutEmailAttempted = false;
        var handler = new FakeHttpMessageHandler(req =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/contacts/batch/upsert"))
            {
                var body = req.Content!.ReadAsStringAsync().Result;
                if (body.Contains("\"email\""))
                    return Json(HttpStatusCode.BadRequest, """{"status":"error","message":"propertyName=email but no recognizable ID here","category":"VALIDATION_ERROR"}""");
                withoutEmailAttempted = true;
                return Json(HttpStatusCode.OK, "{}");
            }
            throw Unexpected(req); // no survivor lookup, no merge attempt — parsing failed before either would run
        });

        var sut = CreateSut(handler);

        await sut.SyncContactAsync(MakeContact("cus_new"));

        Assert.True(withoutEmailAttempted);
    }

    [Fact]
    public async Task SyncContactAsync_On400ThatIsNotAnEmailCollision_NeverRetries()
    {
        var upsertCalls = 0;
        var handler = new FakeHttpMessageHandler(req =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/contacts/batch/upsert"))
            {
                upsertCalls++;
                return Json(HttpStatusCode.BadRequest, """{"status":"error","message":"Property values were not valid: [{propertyName=phone,error=INVALID_PHONE}]","category":"VALIDATION_ERROR"}""");
            }
            throw Unexpected(req);
        });

        var sut = CreateSut(handler);

        await sut.SyncContactAsync(MakeContact("cus_new"));

        Assert.Equal(1, upsertCalls);
    }

    private sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
