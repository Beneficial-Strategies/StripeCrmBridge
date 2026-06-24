using BeneficialStrategies.StripeCrmBridge.Controllers;
using BeneficialStrategies.StripeCrmBridge.HubSpot;
using Serilog;
using Serilog.Sinks.GoogleCloudLogging;
using Stripe;

var isInContainer = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER")
    ?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false;

var logConfig = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext();

if (isInContainer)
{
    var gcpProject = Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT");
    var serviceName = Environment.GetEnvironmentVariable("K_SERVICE") ?? "stripe-crm-bridge";
    var gclOptions = new GoogleCloudLoggingSinkOptions
    {
        ProjectId = gcpProject,
        LogName = "stripe-crm-bridge",
        ResourceType = "cloud_run_revision",
        ServiceName = serviceName
    };
    gclOptions.ResourceLabels["service_name"] = serviceName;
    logConfig = logConfig.WriteTo.GoogleCloudLogging(gclOptions);
}
else
{
    logConfig = logConfig.WriteTo.File(
        path: "logs/crm-.log",
        rollingInterval: RollingInterval.Day,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}");
}

Log.Logger = logConfig.CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog();
    builder.Services.AddControllers();
    builder.Services.AddHttpClient();

    // Stripe — secret key for customer lookups
    StripeConfiguration.ApiKey = builder.Configuration["Stripe:SecretKey"]
        ?? throw new InvalidOperationException("Stripe:SecretKey must be configured");

    // Register Stripe services used by the processor
    builder.Services.AddScoped<CustomerService>();

    // HubSpot CRM integration
    builder.Services.Configure<HubSpotSettings>(builder.Configuration.GetSection(HubSpotSettings.SectionName));
    builder.Services.AddSingleton<IHubSpotService, HubSpotService>();

    // CRM webhook processor
    builder.Services.AddScoped<CrmWebhookProcessor>();

    var app = builder.Build();
    app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
    app.MapControllers();

    var listenUrl = Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://localhost:3002";
    Log.Information("Stripe CRM Bridge starting on {Url}", listenUrl);
    app.Run(listenUrl);
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
