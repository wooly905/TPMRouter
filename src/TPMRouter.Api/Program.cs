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

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRouting();
app.UseMiddleware<ClientApiKeyMiddleware>();

app.MapHealthChecks("/healthz");

app.MapPost("/v1/chat/completions", ChatCompletionsEndpoint.HandleAsync)
    .WithMetadata(new ApiKeyRequirement(ApiKeyScope.Client, OpenAICompatible: true));

app.MapPost("/v1/responses", ResponsesEndpoint.HandleAsync)
    .WithMetadata(new ApiKeyRequirement(ApiKeyScope.Client, OpenAICompatible: true));

app.MapGet("/relay/status", RelayEndpoint.GetStatus)
    .WithMetadata(new ApiKeyRequirement(ApiKeyScope.Admin));

app.MapGet("/relay/dashboard/status", RelayEndpoint.GetStatus);

app.Run();

public partial class Program;
