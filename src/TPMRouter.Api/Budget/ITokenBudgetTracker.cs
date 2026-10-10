namespace TPMRouter.Api.Budget;

public interface ITokenBudgetTracker
{
    /// <summary>
    /// Selects a deployment from the keys of <paramref name="estimates"/> and reserves its estimate, as one atomic step.
    /// </summary>
    ValueTask<ReservationResult> TryReserveAsync(IReadOnlyDictionary<string, long> estimates);

    ValueTask RecordHeadersAsync(string deployment, long? remainingTokens, long? remainingRequests, DateTimeOffset receivedAt);

    ValueTask SetCooldownAsync(string deployment, TimeSpan duration);

    ValueTask<IReadOnlyList<DeploymentStatus>> GetStatusAsync();
}

public sealed record ReservationResult(string? Deployment, long Tokens, TimeSpan? RetryAfter)
{
    public bool Success => Deployment is not null;

    public static ReservationResult Reserved(string deployment, long tokens) => new(deployment, tokens, null);

    public static ReservationResult Full(TimeSpan? retryAfter) => new(null, 0, retryAfter);
}

public sealed record DeploymentStatus(string EndpointId,
                                      string Name,
                                      string Model,
                                      int Priority,
                                      long TokensPerMinute,
                                      int RequestsPerMinute,
                                      long ReservedTokens,
                                      int RequestsInWindow,
                                      long AvailableTokens,
                                      long? AvailableRequests,
                                      long? HeaderRemainingTokens,
                                      long? HeaderRemainingRequests,
                                      DateTimeOffset? CooldownUntil);
