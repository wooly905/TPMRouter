using System.Diagnostics.Metrics;
using TPMRouter.Api.Options;

namespace TPMRouter.Api.Relay;

public sealed class RelayMetrics
{
    public const string MeterName = "TPMRouter";

    private readonly Counter<long> _reservedTokens;
    private readonly Counter<long> _upstream429;
    private readonly Counter<long> _selected;

    public RelayMetrics(IMeterFactory meterFactory)
    {
        Meter meter = meterFactory.Create(MeterName);
        _reservedTokens = meter.CreateCounter<long>("relay.reserved_tokens", "{token}", "Tokens reserved on a deployment.");
        _upstream429 = meter.CreateCounter<long>("relay.upstream_429", "{response}", "429 responses from Azure OpenAI.");
        _selected = meter.CreateCounter<long>("relay.selected_deployment", "{request}", "Requests sent to a deployment.");
    }

    public void Selected(DeploymentTarget target, long reservedTokens)
    {
        KeyValuePair<string, object?> endpoint = new("endpoint", target.EndpointId);
        KeyValuePair<string, object?> deployment = new("deployment", target.Deployment.Name);
        _selected.Add(1, endpoint, deployment);
        _reservedTokens.Add(reservedTokens, endpoint, deployment);
    }

    public void Upstream429(DeploymentTarget target) =>
        _upstream429.Add(1, new KeyValuePair<string, object?>("endpoint", target.EndpointId),
                           new KeyValuePair<string, object?>("deployment", target.Deployment.Name));
}
