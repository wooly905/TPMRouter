using System.Reflection;
using Microsoft.Extensions.Options;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using StackExchange.Redis;
using TPMRouter.Api.Auth;
using TPMRouter.Api.Budget;
using TPMRouter.Api.Endpoints;
using TPMRouter.Api.Options;
using TPMRouter.Api.Relay;
using TPMRouter.Api.Upstream;
using TPMRouter.Api.Usage;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IValidateOptions<RelayOptions>, RelayOptionsValidator>();
builder.Services.AddOptions<RelayOptions>()
    .Bind(builder.Configuration.GetSection(RelayOptions.SectionName))
    .ValidateOnStart();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IUpstreamClient, AzureOpenAIUpstreamClient>();
builder.Services.AddSingleton<TokenEstimator>();
builder.Services.AddSingleton<DeploymentSelector>();
builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
    ConnectionMultiplexer.Connect(sp.GetRequiredService<IOptions<RelayOptions>>().Value.RedisConnectionString));
builder.Services.AddSingleton<InMemoryTokenBudgetTracker>();
builder.Services.AddSingleton<RedisTokenBudgetTracker>();
builder.Services.AddSingleton<ITokenBudgetTracker>(sp =>
    sp.GetRequiredService<IOptions<RelayOptions>>().Value.BudgetStore == BudgetStore.Redis
        ? sp.GetRequiredService<RedisTokenBudgetTracker>()
        : sp.GetRequiredService<InMemoryTokenBudgetTracker>());
builder.Services.AddSingleton<RelayMetrics>();
builder.Services.AddSingleton<RelayService>();
builder.Services.AddSingleton<UsageHistoryStore>();

builder.Services.AddHealthChecks();

bool consoleExporter = builder.Configuration.GetValue<bool>("Telemetry:ConsoleExporter");
builder.Logging.AddOpenTelemetry(o =>
{
    if (consoleExporter)
    {
        o.AddConsoleExporter();
    }
});
builder.Services.AddOpenTelemetry().WithMetrics(m =>
{
    m.AddMeter(RelayMetrics.MeterName);
    if (consoleExporter)
    {
        m.AddConsoleExporter();
    }
});

var app = builder.Build();
app.Services.GetRequiredService<UsageHistoryStore>().Initialize();

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRouting();
app.UseMiddleware<UsageHistoryMiddleware>();
app.UseMiddleware<ClientApiKeyMiddleware>();

app.MapHealthChecks("/healthz");

app.MapPost("/v1/responses", ResponsesEndpoint.HandleAsync)
    .WithMetadata(new ApiKeyRequirement(ApiKeyScope.Client, OpenAICompatible: true));

app.MapGet("/relay/status", RelayEndpoint.GetStatus)
    .WithMetadata(new ApiKeyRequirement(ApiKeyScope.Admin));

app.MapGet("/relay/dashboard/status", RelayEndpoint.GetStatus);

string version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
int metadata = version.IndexOf('+');
// The SDK appends the full git commit hash; a short hash is enough for display.
if (metadata >= 0 && version.Length > metadata + 8)
{
    version = version[..(metadata + 8)];
}

app.MapGet("/relay/version", () => Results.Ok(new { version }));

app.MapGet("/relay/usage-history", UsageHistoryEndpoint.GetHistory)
    .WithMetadata(new ApiKeyRequirement(ApiKeyScope.Admin));

app.Run();

public partial class Program;
