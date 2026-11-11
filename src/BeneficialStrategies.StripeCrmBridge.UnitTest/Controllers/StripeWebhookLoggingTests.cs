using System.Security.Cryptography;
using System.Text;
using BeneficialStrategies.StripeCrmBridge.Controllers;
using BeneficialStrategies.StripeCrmBridge.HubSpot;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Stripe;

namespace BeneficialStrategies.StripeCrmBridge.UnitTest.Controllers;

/// <summary>Every handled event is traceable in the logs by its event ID, and the logs carry IDs only (Iso20022MasterControl#66).</summary>
public class StripeWebhookLoggingTests
{
    private const string Secret = "whsec_test";

    private sealed record Entry(LogLevel Level, string Message, IReadOnlyDictionary<string, object?> Scope);

    // Records each line with the scope it was written in, as a logging provider that supports scopes sees it.
    private sealed class Recorder : ILoggerProvider, ISupportExternalScope, ILogger
    {
        private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();
        public List<Entry> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => this;
        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _scopes.Push(state);
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Dispose() { }

        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var scope = new Dictionary<string, object?>();
            _scopes.ForEachScope((s, d) =>
            {
                if (s is IEnumerable<KeyValuePair<string, object>> pairs) foreach (var p in pairs) d[p.Key] = p.Value;
            }, scope);
            Entries.Add(new Entry(level, formatter(state, exception), scope));
        }
    }

    private static string Signed(string json, out string header)
    {
        var t = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var hash = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes($"{t}.{json}"))).ToLowerInvariant();
        header = $"t={t},v1={hash}";
        return json;
    }

    private static string EventJson(string id, string type, string dataObject) =>
        "{\"id\":\"" + id + "\",\"object\":\"event\",\"api_version\":\"" + StripeConfiguration.ApiVersion +
        "\",\"created\":1760000000,\"livemode\":false,\"type\":\"" + type + "\",\"data\":{\"object\":" + dataObject + "}}";

    private static async Task<(IActionResult Result, Recorder Log)> Post(string json, Mock<IHubSpotService> hubSpot, DefaultHttpContext? context = null)
    {
        var log = new Recorder();
        using var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(log));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Stripe:WebhookSecret"] = Secret }).Build();
        var processor = new CrmWebhookProcessor(hubSpot.Object, new CustomerService(), factory.CreateLogger<CrmWebhookProcessor>());
        var http = context ?? new DefaultHttpContext();
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(Signed(json, out var header)));
        http.Request.Headers["Stripe-Signature"] = header;
        var controller = new StripeWebhookController(processor, config, factory.CreateLogger<StripeWebhookController>())
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
        return (await controller.HandleWebhook(), log);
    }

    [Fact]
    public async Task AHandledEvent_IsLoggedOnArrival_WithItsIdTypeLivemodeAndCreatedTime_AndItsOutcome_UnderTheSameId()
    {
        var hubSpot = new Mock<IHubSpotService>();
        hubSpot.Setup(h => h.IsEnabled).Returns(true);
        var (result, log) = await Post(EventJson("evt_123", "customer.created", """{"id":"cus_9","object":"customer","email":"someone@example.com","name":"Someone"}"""), hubSpot);

        Assert.IsType<OkResult>(result);
        var received = Assert.Single(log.Entries, e => e.Message.StartsWith("Stripe webhook received"));
        Assert.Contains("evt_123", received.Message);
        Assert.Contains("customer.created", received.Message);
        Assert.Contains("livemode=False", received.Message);
        Assert.Contains("created=2025-", received.Message);

        var synced = Assert.Single(log.Entries, e => e.Message.StartsWith("HubSpot contact synced"));
        Assert.Contains("cus_9", synced.Message);
        Assert.Equal("evt_123", synced.Scope["StripeEventId"]);
        Assert.Equal("customer.created", synced.Scope["StripeEventType"]);
    }

    [Fact]
    public async Task AFailedSync_IsAnErrorLine_WithTheEventId_TheStatusAndWhetherARepeatCanCureIt()
    {
        var hubSpot = new Mock<IHubSpotService>();
        hubSpot.Setup(h => h.SyncContactAsync(It.IsAny<ContactSyncData>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HubSpotSyncException("HubSpot answered 503", retryable: true, statusCode: 503));

        var (_, log) = await Post(EventJson("evt_456", "customer.created", """{"id":"cus_7","object":"customer","email":"x@example.com"}"""), hubSpot);

        var failed = Assert.Single(log.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("evt_456", failed.Message);
        Assert.Contains("status=503", failed.Message);
        Assert.Contains("retryable=True", failed.Message);
        Assert.DoesNotContain(log.Entries, e => e.Message.StartsWith("HubSpot contact synced"));
    }

    [Fact]
    public async Task AWebhookWhoseSyncIsOff_SaysNothingWasSent_NotThatItSynced()
    {
        var hubSpot = new Mock<IHubSpotService>();
        hubSpot.Setup(h => h.IsEnabled).Returns(false);

        var (_, log) = await Post(EventJson("evt_off", "customer.created", """{"id":"cus_7","object":"customer"}"""), hubSpot);

        Assert.DoesNotContain(log.Entries, e => e.Message.StartsWith("HubSpot contact synced"));
        Assert.Contains(log.Entries, e => e.Message.StartsWith("HubSpot sync is off"));
    }

    [Fact]
    public async Task AnIgnoredEvent_IsALogLine_WithTheEventId()
    {
        var (result, log) = await Post(EventJson("evt_789", "price.created", """{"id":"price_1","object":"price"}"""), new Mock<IHubSpotService>());

        Assert.IsType<OkResult>(result);
        var ignored = Assert.Single(log.Entries, e => e.Message.StartsWith("CRM ignored event"));
        Assert.Equal("evt_789", ignored.Scope["StripeEventId"]);
    }

    [Fact]
    public async Task NoLogLine_CarriesAnEmailAddressOrAName()
    {
        var (_, log) = await Post(EventJson("evt_1", "customer.created", """{"id":"cus_1","object":"customer","email":"someone@example.com","name":"Someone Else"}"""), new Mock<IHubSpotService>());

        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("someone@example.com") || e.Message.Contains("Someone Else"));
    }

    // -------------------------------------------------------------------------
    // What Stripe is answered when the sync does not land: an error it can recognize, never 200
    // -------------------------------------------------------------------------

    private static Mock<IHubSpotService> HubSpotFailing(HubSpotSyncException failure)
    {
        var hubSpot = new Mock<IHubSpotService>();
        hubSpot.Setup(h => h.SyncContactAsync(It.IsAny<ContactSyncData>(), It.IsAny<CancellationToken>())).ThrowsAsync(failure);
        return hubSpot;
    }

    [Fact]
    public async Task ASyncAHubSpotRetryCanCure_IsAnsweredWith503_ARetryAfter_AndABodyThatNamesTheCause()
    {
        var http = new DefaultHttpContext();
        var (result, _) = await Post(EventJson("evt_1", "customer.created", """{"id":"cus_1","object":"customer"}"""),
            HubSpotFailing(new HubSpotSyncException("HubSpot answered 429", retryable: true, statusCode: 429, retryAfter: TimeSpan.FromSeconds(30))), http);

        var answer = Assert.IsType<ObjectResult>(result);
        Assert.Equal(503, answer.StatusCode);
        Assert.Equal("30", http.Response.Headers.RetryAfter.ToString());
        var body = System.Text.Json.JsonSerializer.Serialize(answer.Value);
        Assert.Contains("hubspot_sync_failed", body);
        Assert.Contains("\"retryable\":true", body);
        Assert.Contains("429", body);
    }

    [Fact]
    public async Task ASyncHubSpotAnswerWithoutARetryAfter_GetsADefaultOfAMinute()
    {
        var http = new DefaultHttpContext();
        await Post(EventJson("evt_1", "customer.created", """{"id":"cus_1","object":"customer"}"""),
            HubSpotFailing(new HubSpotSyncException("HubSpot could not be reached", retryable: true, statusCode: null)), http);

        Assert.Equal("60", http.Response.Headers.RetryAfter.ToString());
    }

    [Fact]
    public async Task ASyncHubSpotRefused_IsAnsweredWith502_AndNoRetryAfter()
    {
        var http = new DefaultHttpContext();
        var (result, _) = await Post(EventJson("evt_1", "customer.created", """{"id":"cus_1","object":"customer"}"""),
            HubSpotFailing(new HubSpotSyncException("HubSpot answered 401", retryable: false, statusCode: 401)), http);

        var answer = Assert.IsType<ObjectResult>(result);
        Assert.Equal(502, answer.StatusCode);
        Assert.False(http.Response.Headers.ContainsKey("Retry-After"));
        Assert.Contains("\"retryable\":false", System.Text.Json.JsonSerializer.Serialize(answer.Value));
    }

    [Fact]
    public async Task AnUnexpectedFailure_IsLoggedWithTheEventId_AndStillFailsTheRequest()
    {
        var hubSpot = new Mock<IHubSpotService>();
        hubSpot.Setup(h => h.SyncContactAsync(It.IsAny<ContactSyncData>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("bug"));
        var log = new Recorder();
        using var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(log));
        var processor = new CrmWebhookProcessor(hubSpot.Object, new CustomerService(), factory.CreateLogger<CrmWebhookProcessor>());
        var json = EventJson("evt_bug", "customer.created", """{"id":"cus_1","object":"customer"}""");
        var http = new DefaultHttpContext();
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(Signed(json, out var header)));
        http.Request.Headers["Stripe-Signature"] = header;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Stripe:WebhookSecret"] = Secret }).Build();
        var controller = new StripeWebhookController(processor, config, factory.CreateLogger<StripeWebhookController>())
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.HandleWebhook());

        Assert.Contains("evt_bug", Assert.Single(log.Entries, e => e.Level == LogLevel.Error).Message);
    }
}
