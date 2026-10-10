namespace TPMRouter.Api.Options;

public sealed class EndpointOptions
{
    public string Id { get; set; } = string.Empty;

    public string Endpoint { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public List<DeploymentOptions> Deployments { get; set; } = [];
}
