using System.Net;
using System.Text;
using System.Text.Json;
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

    /// <summary>The lookup of a contact's names before a sync writes them (#65): a search with an IN filter.</summary>
    private static bool IsNameLookup(HttpRequestMessage req) =>
        req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/contacts/search") &&
        req.Content!.ReadAsStringAsync().Result.Contains("\"IN\"", StringComparison.Ordinal);

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
            if (IsNameLookup(req)) return Json(HttpStatusCode.OK, """{"results":[]}"""); // the name lookup of #65: no contact yet
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
    // staging email decoration — HubSpot's uniqueness is portal-wide, so a real
    // address reused across staging test personas (or with its own production
    // account) would otherwise collide
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SyncContactAsync_InStaging_DecoratesEmailWithStripeCustomerIdTag()
    {
        string? capturedBody = null;
        var handler = new FakeHttpMessageHandler(req =>
        {
            if (IsNameLookup(req)) return Json(HttpStatusCode.OK, """{"results":[]}"""); // the name lookup of #65: no contact yet
            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/contacts/batch/upsert"))
            {
                capturedBody = req.Content!.ReadAsStringAsync().Result;
                return Json(HttpStatusCode.OK, "{}");
            }
            throw Unexpected(req);
        });

        var sut = CreateSut(handler, new HubSpotSettings { Enabled = true, AccessToken = "test-token", Environment = "staging" });

        await sut.SyncContactAsync(MakeContact("cus_USr3b7NQ4Sxnzb", "beneficialstrategies@gmail.com"));

        Assert.Equal("beneficialstrategies+stg-USr3b7NQ4Sxnzb@gmail.com", EmailFromUpsertBody(capturedBody));
    }

    [Fact]
    public async Task SyncContactAsync_InProduction_NeverDecoratesEmail()
    {
        string? capturedBody = null;
        var handler = new FakeHttpMessageHandler(req =>
        {
            if (IsNameLookup(req)) return Json(HttpStatusCode.OK, """{"results":[]}"""); // the name lookup of #65: no contact yet
            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/contacts/batch/upsert"))
            {
                capturedBody = req.Content!.ReadAsStringAsync().Result;
                return Json(HttpStatusCode.OK, "{}");
            }
            throw Unexpected(req);
        });

        var sut = CreateSut(handler, new HubSpotSettings { Enabled = true, AccessToken = "test-token", Environment = "production" });

        await sut.SyncContactAsync(MakeContact("cus_VBMh8bNt7PsFzm", "skippy@possumshanks.com"));

        Assert.Equal("skippy@possumshanks.com", EmailFromUpsertBody(capturedBody));
    }

    private static string? EmailFromUpsertBody(string? body)
    {
        using var doc = JsonDocument.Parse(body!);
        return doc.RootElement.GetProperty("inputs")[0].GetProperty("properties").GetProperty("email").GetString();
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
            if (IsNameLookup(req)) return Json(HttpStatusCode.OK, """{"results":[]}"""); // the name lookup of #65: no contact yet
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
            if (IsNameLookup(req)) return Json(HttpStatusCode.OK, """{"results":[]}"""); // the name lookup of #65: no contact yet
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
            if (IsNameLookup(req)) return Json(HttpStatusCode.OK, """{"results":[]}"""); // the name lookup of #65: no contact yet
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
            if (IsNameLookup(req)) return Json(HttpStatusCode.OK, """{"results":[]}"""); // the name lookup of #65: no contact yet
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

    // -------------------------------------------------------------------------
    // Names (#65): the display name seeds First Name and Last Name, never over a person's edit
    // -------------------------------------------------------------------------

    /// <summary>Answers the name lookup (a search by stripe_customer_id) and the upsert, and keeps the upsert body.</summary>
    private sealed class NameSyncServer(string lookupResults, HttpStatusCode lookupStatus = HttpStatusCode.OK)
    {
        public List<string> Upserts { get; } = [];
        public int Lookups { get; private set; }

        public HttpResponseMessage Handle(HttpRequestMessage req)
        {
            var path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Post && path.EndsWith("/contacts/search"))
            {
                Lookups++;
                return Json(lookupStatus, lookupResults);
            }
            if (req.Method == HttpMethod.Post && path.EndsWith("/contacts/batch/upsert"))
            {
                Upserts.Add(req.Content!.ReadAsStringAsync().Result);
                return Json(HttpStatusCode.OK, "{}");
            }
            throw Unexpected(req);
        }
    }

    private static JsonElement PropertiesOfFirstUpsert(NameSyncServer server)
    {
        var body = JsonDocument.Parse(Assert.Single(server.Upserts));
        return body.RootElement.GetProperty("inputs")[0].GetProperty("properties").Clone();
    }

    [Fact]
    public async Task Sync_NewContact_WritesTheDisplayNameSplitIntoBothNames()
    {
        var server = new NameSyncServer("""{"results":[]}""");
        var sut = CreateSut(new FakeHttpMessageHandler(server.Handle));

        await sut.SyncContactAsync(MakeContact("cus_new") with { DisplayName = "Nancy Ward" });

        var props = PropertiesOfFirstUpsert(server);
        Assert.Equal("Nancy", props.GetProperty("firstname").GetString());
        Assert.Equal("Ward", props.GetProperty("lastname").GetString());
    }

    [Fact]
    public async Task Sync_ContactWithBothNamesEmpty_WritesTheNames()
    {
        var server = new NameSyncServer("""{"results":[{"id":"1","properties":{"stripe_customer_id":"cus_1","firstname":null,"lastname":null}}]}""");
        var sut = CreateSut(new FakeHttpMessageHandler(server.Handle));

        await sut.SyncContactAsync(MakeContact("cus_1") with { DisplayName = "Mary Ann Smith" });

        var props = PropertiesOfFirstUpsert(server);
        Assert.Equal("Mary Ann", props.GetProperty("firstname").GetString());
        Assert.Equal("Smith", props.GetProperty("lastname").GetString());
    }

    [Fact]
    public async Task Sync_ReplayedCustomerCreated_ForAContactWhoseNamesWereCorrectedByHand_KeepsThemAndTheRestStillSyncs()
    {
        // Nancy Ward, split by hand on 2026-11-01; the customer.created event replayed on 2026-11-05 undid it (#65).
        var server = new NameSyncServer("""{"results":[{"id":"1","properties":{"stripe_customer_id":"cus_1","firstname":"Nancy","lastname":"Ward"}}]}""");
        var sut = CreateSut(new FakeHttpMessageHandler(server.Handle));

        await sut.SyncContactAsync(MakeContact("cus_1") with { DisplayName = "Nancy Ward" });

        var props = PropertiesOfFirstUpsert(server);
        Assert.False(props.TryGetProperty("firstname", out _));
        Assert.False(props.TryGetProperty("lastname", out _));
        Assert.Equal("active", props.GetProperty("subscription_status").GetString());
    }

    [Theory]
    [InlineData("Nancy", null)]
    [InlineData(null, "Ward")]
    public async Task Sync_ContactWithEitherNameSet_KeepsBoth(string? first, string? last)
    {
        var found = JsonSerializer.Serialize(new
        {
            results = new[] { new { id = "1", properties = new { stripe_customer_id = "cus_1", firstname = first, lastname = last } } }
        });
        var server = new NameSyncServer(found);
        var sut = CreateSut(new FakeHttpMessageHandler(server.Handle));

        await sut.SyncContactAsync(MakeContact("cus_1") with { DisplayName = "Someone Else" });

        var props = PropertiesOfFirstUpsert(server);
        Assert.False(props.TryGetProperty("firstname", out _));
        Assert.False(props.TryGetProperty("lastname", out _));
    }

    [Fact]
    public async Task Sync_WhenTheNameLookupFails_LeavesTheNamesAloneButStillSyncs()
    {
        var server = new NameSyncServer("{}", HttpStatusCode.InternalServerError);
        var sut = CreateSut(new FakeHttpMessageHandler(server.Handle));

        await sut.SyncContactAsync(MakeContact("cus_1") with { DisplayName = "Nancy Ward" });

        var props = PropertiesOfFirstUpsert(server);
        Assert.False(props.TryGetProperty("firstname", out _));
        Assert.Equal("active", props.GetProperty("subscription_status").GetString());
    }

    [Fact]
    public async Task BulkSync_ReadsTheNamesOfAWholeBatchInOneLookup()
    {
        var server = new NameSyncServer("""
            {"results":[{"id":"1","properties":{"stripe_customer_id":"cus_a","firstname":"Hand","lastname":"Edited"}}]}
            """);
        var sut = CreateSut(new FakeHttpMessageHandler(server.Handle));

        await sut.BulkSyncContactsAsync([
            MakeContact("cus_a", "a@example.com") with { DisplayName = "Auto Name" },
            MakeContact("cus_b", "b@example.com") with { DisplayName = "New Person" }]);

        Assert.Equal(1, server.Lookups);
        var inputs = JsonDocument.Parse(Assert.Single(server.Upserts)).RootElement.GetProperty("inputs");
        Assert.False(inputs[0].GetProperty("properties").TryGetProperty("firstname", out _));   // cus_a keeps its names
        Assert.Equal("New", inputs[1].GetProperty("properties").GetProperty("firstname").GetString());
        Assert.Equal("Person", inputs[1].GetProperty("properties").GetProperty("lastname").GetString());
    }

    private sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
