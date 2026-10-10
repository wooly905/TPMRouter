using TPMRouter.Api.Options;

namespace TPMRouter.Api.Relay;

/// <param name="AvailableRequests">Null when the deployment has no local RPM limit and no header snapshot.</param>
public sealed record DeploymentBudget(DeploymentTarget Target, long AvailableTokens, long? AvailableRequests, bool InCooldown)
{
    public DeploymentOptions Deployment => Target.Deployment;
}

public sealed class DeploymentSelector
{
    public DeploymentBudget? Select(IEnumerable<DeploymentBudget> budgets, IReadOnlyDictionary<string, long> estimates)
    {
        return budgets
            .Where(b => !b.InCooldown)
            .Where(b => estimates.TryGetValue(b.Target.Key, out long estimate) && b.AvailableTokens >= estimate)
            .Where(b => b.AvailableRequests is null || b.AvailableRequests >= 1)
            .OrderBy(b => b.Deployment.Priority)
            .ThenByDescending(RemainingRatio)
            .ThenByDescending(b => b.AvailableTokens)
            .FirstOrDefault();
    }

    public static double RemainingRatio(DeploymentBudget budget)
    {
        double tokenRatio = (double)budget.AvailableTokens / budget.Deployment.TokensPerMinute!.Value;
        int rpm = budget.Deployment.RequestsPerMinute!.Value;

        if (rpm == 0 || budget.AvailableRequests is null)
        {
            return tokenRatio;
        }

        return Math.Min(tokenRatio, (double)budget.AvailableRequests.Value / rpm);
    }

    public static bool FitsDeployment(long estimate, DeploymentOptions deployment, double safetyFactor) =>
        estimate <= deployment.TokensPerMinute!.Value * safetyFactor;
}
