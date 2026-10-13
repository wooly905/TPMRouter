using System.Diagnostics.Metrics;
using System.ClientModel.Primitives;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TPMRouter.Api.Budget;
using TPMRouter.Api.Options;
using TPMRouter.Api.Relay;
using TPMRouter.Api.Upstream;

namespace TPMRouter.UnitTests;

internal static class TestData
{
    public static DeploymentOptions Deployment(string name,
                                               long tpm,
                                               int rpm,
                                               int maxOutputTokens = 100,
                                               int priority = 1,
                                               bool supportsVision = false) =>
        new() { Name = name, Model = name, TokensPerMinute = tpm, RequestsPerMinute = rpm, MaxOutputTokens = maxOutputTokens, Priority = priority, SupportsVision = supportsVision };

    public static DeploymentTarget Target(DeploymentOptions deployment, string endpointId = "test") => new(endpointId, deployment);

    public static EndpointOptions Endpoint(string id, params DeploymentOptions[] deployments) => new()
    {
        Id = id,
        Endpoint = $"https://{id}.example.invalid/",
        ApiKey = $"{id}-key",
        Deployments = [.. deployments],
    };

    public static RelayOptions Options(double safetyFactor, params DeploymentOptions[] deployments) => new()
    {
        SafetyFactor = safetyFactor,
        ClientApiKey = "client-key",
        AdminApiKey = "admin-key",
        Endpoints = [new() { Id = "test", Endpoint = "https://example.invalid/", ApiKey = "upstream-key", Deployments = [.. deployments] }],
    };

    public static InMemoryTokenBudgetTracker Tracker(RelayOptions options, TimeProvider time) =>
        new(Microsoft.Extensions.Options.Options.Create(options), new DeploymentSelector(), time);

    public static RelayService Service(RelayOptions options, IUpstreamClient upstream, TimeProvider? time = null, ITokenBudgetTracker? tracker = null)
    {
        time ??= new FakeTimeProvider();
        IMeterFactory meterFactory = new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>();
        return new RelayService(
            Microsoft.Extensions.Options.Options.Create(options),
            new TokenEstimator(),
            tracker ?? Tracker(options, time),
            upstream,
            new RelayMetrics(meterFactory),
            time,
            NullLogger<RelayService>.Instance);
    }
}

internal sealed class FakeUpstreamClient(Func<string, UpstreamResult> respond) : IUpstreamClient
{
    public List<string> Calls { get; } = [];

    public static UpstreamResult Ok() => new(200, null, default, BinaryData.FromString("{}"));

    public Task<UpstreamResult> CreateResponseAsync(string deployment, JsonObject request, Func<PipelineResponse, CancellationToken, Task> onStream, CancellationToken cancellationToken)
    {
        Calls.Add(deployment);
        return Task.FromResult(respond(deployment));
    }
}
