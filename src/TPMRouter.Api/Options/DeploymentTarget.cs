namespace TPMRouter.Api.Options;

public sealed record DeploymentTarget(string EndpointId, DeploymentOptions Deployment)
{
    // Escape each component so identifiers containing separators cannot collide.
    public string Key { get; } = $"{Uri.EscapeDataString(EndpointId)}/{Uri.EscapeDataString(Deployment.Name)}";
}
