namespace TPMRouter.Api.Options;

public sealed class RelayOptions
{
    public const string SectionName = "Relay";

    public double SafetyFactor { get; set; } = 0.9;

    public int MaxQueueWaitSeconds { get; set; } = 30;

    public int ErrorCooldownSeconds { get; set; } = 10;

    public int UpstreamTimeoutSeconds { get; set; } = 120;

    public int RequestDeadlineSeconds { get; set; } = 180;

    public string ClientApiKey { get; set; } = string.Empty;

    public string AdminApiKey { get; set; } = string.Empty;

    public BudgetStore BudgetStore { get; set; } = BudgetStore.InMemory;

    public string RedisConnectionString { get; set; } = string.Empty;

    public string RedisKeyPrefix { get; set; } = "tpmrouter";

    public List<EndpointOptions> Endpoints { get; set; } = [];

    public IEnumerable<DeploymentTarget> GetDeploymentTargets() =>
        Endpoints.SelectMany(e => e.Deployments.Select(d => new DeploymentTarget(e.Id, d)));
}

public enum BudgetStore
{
    InMemory,
    Redis,
}

public sealed class DeploymentOptions
{
    public string Name { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    // No defaults: limits must come from configuration.
    public long? TokensPerMinute { get; set; }

    public int? RequestsPerMinute { get; set; }

    public int MaxOutputTokens { get; set; }

    public int Priority { get; set; }

    public bool SupportsVision { get; set; }
}
