using Microsoft.Extensions.Options;
using TPMRouter.Api.Options;
using TPMRouter.Api.Relay;

namespace TPMRouter.Api.Budget;

public sealed class InMemoryTokenBudgetTracker : ITokenBudgetTracker
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    private readonly Lock _lock = new();
    private readonly Dictionary<string, DeploymentState> _states;
    private readonly double _safetyFactor;
    private readonly DeploymentSelector _selector;
    private readonly TimeProvider _timeProvider;

    public InMemoryTokenBudgetTracker(IOptions<RelayOptions> options, DeploymentSelector selector, TimeProvider timeProvider)
    {
        _safetyFactor = options.Value.SafetyFactor;
        _selector = selector;
        _timeProvider = timeProvider;
        _states = options.Value.GetDeploymentTargets().ToDictionary(d => d.Key, d => new DeploymentState(d), StringComparer.Ordinal);
    }

    ValueTask<ReservationResult> ITokenBudgetTracker.TryReserveAsync(IReadOnlyDictionary<string, long> estimates) =>
        ValueTask.FromResult(TryReserve(estimates));

    ValueTask ITokenBudgetTracker.RecordHeadersAsync(string deployment, long? remainingTokens, long? remainingRequests, DateTimeOffset receivedAt)
    {
        RecordHeaders(deployment, remainingTokens, remainingRequests, receivedAt);
        return ValueTask.CompletedTask;
    }

    ValueTask ITokenBudgetTracker.SetCooldownAsync(string deployment, TimeSpan duration)
    {
        SetCooldown(deployment, duration);
        return ValueTask.CompletedTask;
    }

    ValueTask<IReadOnlyList<DeploymentStatus>> ITokenBudgetTracker.GetStatusAsync() => ValueTask.FromResult(GetStatus());

    ValueTask ITokenBudgetTracker.RecordUsageAsync(string deployment, long actualTokens)
    {
        RecordUsage(deployment, actualTokens);
        return ValueTask.CompletedTask;
    }

    public void RecordUsage(string deployment, long actualTokens)
    {
        lock (_lock)
        {
            if (_states.TryGetValue(deployment, out DeploymentState? state))
            {
                state.Usages.Enqueue(new Usage(_timeProvider.GetUtcNow(), actualTokens));
            }
        }
    }

    public ReservationResult TryReserve(IReadOnlyDictionary<string, long> estimates)
    {
        lock (_lock)
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            List<DeploymentState> candidates = [];
            List<DeploymentBudget> budgets = [];
            foreach (string name in estimates.Keys)
            {
                if (_states.TryGetValue(name, out DeploymentState? state))
                {
                    state.Prune(now);
                    candidates.Add(state);
                    budgets.Add(GetBudget(state, now));
                }
            }

            DeploymentBudget? selected = _selector.Select(budgets, estimates);
            if (selected is null)
            {
                return ReservationResult.Full(NextChange(candidates, now));
            }

            string deployment = selected.Target.Key;
            long tokens = estimates[deployment];
            _states[deployment].Reserve(now, tokens);
            return ReservationResult.Reserved(deployment, tokens);
        }
    }

    public void RecordHeaders(string deployment, long? remainingTokens, long? remainingRequests, DateTimeOffset receivedAt)
    {
        lock (_lock)
        {
            if (!_states.TryGetValue(deployment, out DeploymentState? state))
            {
                return;
            }

            // Parallel responses can arrive out of order: keep only the newest snapshot.
            if (remainingTokens is { } tokens && (state.TokenSnapshot is null || state.TokenSnapshot.Value.ReceivedAt < receivedAt))
            {
                state.TokenSnapshot = new Snapshot(tokens, receivedAt);
            }

            if (remainingRequests is { } requests && (state.RequestSnapshot is null || state.RequestSnapshot.Value.ReceivedAt < receivedAt))
            {
                state.RequestSnapshot = new Snapshot(requests, receivedAt);
            }
        }
    }

    public void SetCooldown(string deployment, TimeSpan duration)
    {
        lock (_lock)
        {
            if (_states.TryGetValue(deployment, out DeploymentState? state))
            {
                DateTimeOffset until = _timeProvider.GetUtcNow() + duration;
                if (until > state.CooldownUntil)
                {
                    state.CooldownUntil = until;
                }
            }
        }
    }

    public IReadOnlyList<DeploymentStatus> GetStatus()
    {
        lock (_lock)
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            List<DeploymentStatus> result = [];
            foreach (DeploymentState state in _states.Values)
            {
                state.Prune(now);
                DeploymentBudget budget = GetBudget(state, now);
                result.Add(new DeploymentStatus(
                    state.Target.EndpointId,
                    state.Deployment.Name,
                    state.Deployment.Model,
                    state.Deployment.Priority,
                    state.Deployment.TokensPerMinute!.Value,
                    state.Deployment.RequestsPerMinute!.Value,
                    state.ReservedTokens,
                    state.Reservations.Count,
                    budget.AvailableTokens,
                    budget.AvailableRequests,
                    state.TokenSnapshot?.Remaining,
                    state.RequestSnapshot?.Remaining,
                    budget.InCooldown ? state.CooldownUntil : null,
                    state.Usages.Sum(u => u.Tokens)));
            }

            return result;
        }
    }

    private DeploymentBudget GetBudget(DeploymentState state, DateTimeOffset now)
    {
        long tpm = state.Deployment.TokensPerMinute!.Value;
        int rpm = state.Deployment.RequestsPerMinute!.Value;

        long availableTokens = (long)Math.Floor(tpm * _safetyFactor) - state.ReservedTokens;
        if (state.TokenSnapshot is { } tokenSnapshot)
        {
            long headerAvailable = tokenSnapshot.Remaining - state.TokensReservedAfter(tokenSnapshot.ReceivedAt);
            availableTokens = Math.Min(availableTokens, headerAvailable);
        }

        long? availableRequests = rpm > 0 ? (long)Math.Floor(rpm * _safetyFactor) - state.Reservations.Count : null;
        if (state.RequestSnapshot is { } requestSnapshot)
        {
            long headerAvailable = requestSnapshot.Remaining - state.RequestsAfter(requestSnapshot.ReceivedAt);
            availableRequests = availableRequests is null ? headerAvailable : Math.Min(availableRequests.Value, headerAvailable);
        }

        return new DeploymentBudget(state.Target, availableTokens, availableRequests, state.CooldownUntil > now);
    }

    private static TimeSpan? NextChange(List<DeploymentState> candidates, DateTimeOffset now)
    {
        DateTimeOffset? next = null;
        foreach (DeploymentState state in candidates)
        {
            Consider(state.Reservations.Count > 0 ? state.Reservations.Peek().At + Window : null);
            Consider(state.CooldownUntil > now ? state.CooldownUntil : null);
            Consider(state.TokenSnapshot?.ReceivedAt + Window);
            Consider(state.RequestSnapshot?.ReceivedAt + Window);
        }

        return next is null ? null : next.Value - now;

        void Consider(DateTimeOffset? at)
        {
            if (at is { } value && value > now && (next is null || value < next))
            {
                next = value;
            }
        }
    }

    private readonly record struct Reservation(DateTimeOffset At, long Tokens);

    private readonly record struct Snapshot(long Remaining, DateTimeOffset ReceivedAt);

    private readonly record struct Usage(DateTimeOffset At, long Tokens);

    private sealed class DeploymentState(DeploymentTarget target)
    {
        public DeploymentTarget Target { get; } = target;

        public DeploymentOptions Deployment => Target.Deployment;

        public Queue<Reservation> Reservations { get; } = new();

        public Queue<Usage> Usages { get; } = new();

        public long ReservedTokens { get; private set; }

        public Snapshot? TokenSnapshot { get; set; }

        public Snapshot? RequestSnapshot { get; set; }

        public DateTimeOffset CooldownUntil { get; set; }

        public void Reserve(DateTimeOffset at, long tokens)
        {
            Reservations.Enqueue(new Reservation(at, tokens));
            ReservedTokens += tokens;
        }

        public void Prune(DateTimeOffset now)
        {
            while (Reservations.Count > 0 && Reservations.Peek().At + Window <= now)
            {
                ReservedTokens -= Reservations.Dequeue().Tokens;
            }

            while (Usages.Count > 0 && Usages.Peek().At + Window <= now)
            {
                Usages.Dequeue();
            }

            if (TokenSnapshot is { } tokens && tokens.ReceivedAt + Window <= now)
            {
                TokenSnapshot = null;
            }

            if (RequestSnapshot is { } requests && requests.ReceivedAt + Window <= now)
            {
                RequestSnapshot = null;
            }
        }

        public long TokensReservedAfter(DateTimeOffset at)
        {
            long sum = 0;
            foreach (Reservation reservation in Reservations)
            {
                if (reservation.At > at)
                {
                    sum += reservation.Tokens;
                }
            }

            return sum;
        }

        public int RequestsAfter(DateTimeOffset at)
        {
            int count = 0;
            foreach (Reservation reservation in Reservations)
            {
                if (reservation.At > at)
                {
                    count++;
                }
            }

            return count;
        }
    }
}
