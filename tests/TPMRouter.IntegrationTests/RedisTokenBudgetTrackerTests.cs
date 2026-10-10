using Microsoft.Extensions.Time.Testing;
using StackExchange.Redis;
using Testcontainers.Redis;
using TPMRouter.Api.Budget;
using TPMRouter.Api.Options;

namespace TPMRouter.IntegrationTests;

public sealed class RedisFixture : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder("redis:7.4").Build();

    public string ConnectionString => _container.GetConnectionString();

    public ConnectionMultiplexer Connection { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        Connection = await ConnectionMultiplexer.ConnectAsync(ConnectionString);
    }

    public async Task DisposeAsync()
    {
        await Connection.DisposeAsync();
        await _container.DisposeAsync();
    }
}

// Container startup loads the machine; run alone so timing-based tests in other classes stay stable.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RedisCollection : ICollectionFixture<RedisFixture>
{
    public const string Name = "Redis";
}

[Collection(RedisCollection.Name)]
public sealed class RedisTokenBudgetTrackerTests(RedisFixture redis)
{
    private readonly FakeTimeProvider _time = new();

    [Theory]
    [InlineData("reservation")]
    [InlineData("tokens")]
    [InlineData("requests")]
    [InlineData("cooldown")]
    public async Task SameNamedDeployments_KeepBudgetAndCooldownSeparate(string limit)
    {
        RelayOptions options = new()
        {
            SafetyFactor = 1.0,
            RedisKeyPrefix = $"multi-{Guid.NewGuid():N}",
            Endpoints =
            [
                new() { Id = "first", Deployments = [Deployment("shared", 1000, 0)] },
                new() { Id = "second", Deployments = [Deployment("shared", 1000, 0)] },
            ],
        };
        var tracker = new RedisTokenBudgetTracker(redis.Connection, Microsoft.Extensions.Options.Options.Create(options), _time);
        var otherInstance = new RedisTokenBudgetTracker(redis.Connection, Microsoft.Extensions.Options.Options.Create(options), _time);
        switch (limit)
        {
            case "reservation":
                Assert.True((await tracker.TryReserveAsync(new Dictionary<string, long> { ["first/shared"] = 1000 })).Success);
                break;
            case "tokens":
                await tracker.RecordHeadersAsync("first/shared", 0, null, _time.GetUtcNow());
                break;
            case "requests":
                await tracker.RecordHeadersAsync("first/shared", null, 0, _time.GetUtcNow());
                break;
            case "cooldown":
                await tracker.SetCooldownAsync("first/shared", TimeSpan.FromSeconds(30));
                break;
        }

        ReservationResult result = await otherInstance.TryReserveAsync(new Dictionary<string, long>
        {
            ["first/shared"] = 1000,
            ["second/shared"] = 1000,
        });

        Assert.Equal("second/shared", result.Deployment);
        Assert.Equal(1000, (await tracker.GetStatusAsync()).Single(s => s.EndpointId == "second").ReservedTokens);
        Assert.All(await tracker.GetStatusAsync(), s => Assert.Equal("shared", s.Model));
    }

    private RedisTokenBudgetTracker Tracker(double safetyFactor, params DeploymentOptions[] deployments)
    {
        RelayOptions options = new()
        {
            SafetyFactor = safetyFactor,
            RedisKeyPrefix = $"test-{Guid.NewGuid():N}",
            Endpoints = [new() { Id = "test", Deployments = [.. deployments] }],
        };
        return new RedisTokenBudgetTracker(redis.Connection, Microsoft.Extensions.Options.Options.Create(options), _time);
    }

    private static DeploymentOptions Deployment(string name, long tpm, int rpm, int priority = 1) =>
        new() { Name = name, Model = name, TokensPerMinute = tpm, RequestsPerMinute = rpm, MaxOutputTokens = 100, Priority = priority };

    [Fact]
    public async Task Reservation_ExpiresAfter60Seconds()
    {
        var tracker = Tracker(1.0, Deployment("a", 1000, 0));
        Dictionary<string, long> estimates = new() { ["test/a"] = 1000 };

        Assert.True((await tracker.TryReserveAsync(estimates)).Success);
        ReservationResult full = await tracker.TryReserveAsync(estimates);
        Assert.False(full.Success);
        Assert.Equal(TimeSpan.FromSeconds(60), full.RetryAfter);

        _time.Advance(TimeSpan.FromSeconds(59));
        Assert.False((await tracker.TryReserveAsync(estimates)).Success);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.True((await tracker.TryReserveAsync(estimates)).Success);
    }

    [Fact]
    public async Task Select_FollowsPriorityAndRpm()
    {
        var tracker = Tracker(1.0, Deployment("preferred", 1000, 1, priority: 1), Deployment("other", 1000, 0, priority: 2));
        Dictionary<string, long> estimates = new() { ["test/preferred"] = 10, ["test/other"] = 10 };

        Assert.Equal("test/preferred", (await tracker.TryReserveAsync(estimates)).Deployment);
        // RPM of "preferred" is now full.
        Assert.Equal("test/other", (await tracker.TryReserveAsync(estimates)).Deployment);
    }

    [Fact]
    public async Task HeaderSnapshot_ReplacesLocalValueAndSubtractsLaterReservations()
    {
        var tracker = Tracker(1.0, Deployment("a", 1000, 0));
        await tracker.RecordHeadersAsync("test/a", remainingTokens: 500, remainingRequests: null, _time.GetUtcNow());
        Assert.Equal(500, (await tracker.GetStatusAsync())[0].AvailableTokens);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.True((await tracker.TryReserveAsync(new Dictionary<string, long> { ["test/a"] = 200 })).Success);

        Assert.Equal(300, (await tracker.GetStatusAsync())[0].AvailableTokens);
        Assert.False((await tracker.TryReserveAsync(new Dictionary<string, long> { ["test/a"] = 301 })).Success);
    }

    [Fact]
    public async Task HeaderSnapshot_OlderDoesNotReplaceNewer()
    {
        var tracker = Tracker(1.0, Deployment("a", 1000, 10, priority: 9));
        DateTimeOffset older = _time.GetUtcNow();

        await tracker.RecordHeadersAsync("test/a", remainingTokens: 800, remainingRequests: 8, older.AddSeconds(1));
        await tracker.RecordHeadersAsync("test/a", remainingTokens: 100, remainingRequests: 1, older);

        DeploymentStatus status = (await tracker.GetStatusAsync())[0];
        Assert.Equal(9, status.Priority);
        Assert.Equal(800, status.HeaderRemainingTokens);
        Assert.Equal(8, status.HeaderRemainingRequests);
    }

    [Fact]
    public async Task HeaderSnapshot_ExpiresAfter60Seconds()
    {
        var tracker = Tracker(1.0, Deployment("a", 1000, 0));
        await tracker.RecordHeadersAsync("test/a", remainingTokens: 0, remainingRequests: null, _time.GetUtcNow());
        Assert.Equal(0, (await tracker.GetStatusAsync())[0].AvailableTokens);

        _time.Advance(TimeSpan.FromSeconds(60));

        DeploymentStatus status = (await tracker.GetStatusAsync())[0];
        Assert.Null(status.HeaderRemainingTokens);
        Assert.Equal(1000, status.AvailableTokens);
    }

    [Fact]
    public async Task Cooldown_BlocksDeploymentUntilItEnds()
    {
        var tracker = Tracker(1.0, Deployment("a", 1000, 0));
        await tracker.SetCooldownAsync("test/a", TimeSpan.FromSeconds(5));

        ReservationResult blocked = await tracker.TryReserveAsync(new Dictionary<string, long> { ["test/a"] = 1 });
        Assert.False(blocked.Success);
        Assert.Equal(TimeSpan.FromSeconds(5), blocked.RetryAfter);
        Assert.NotNull((await tracker.GetStatusAsync())[0].CooldownUntil);

        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.True((await tracker.TryReserveAsync(new Dictionary<string, long> { ["test/a"] = 1 })).Success);
    }

    [Fact]
    public async Task ParallelReservations_DoNotExceedTpm()
    {
        var tracker = Tracker(0.9, Deployment("a", 10_000, 0), Deployment("b", 5_000, 0));
        Dictionary<string, long> estimates = new() { ["test/a"] = 300, ["test/b"] = 300 };

        ReservationResult[] results = await Task.WhenAll(
            Enumerable.Range(0, 100).Select(_ => tracker.TryReserveAsync(estimates).AsTask()));

        Assert.Equal(30, results.Count(r => r.Deployment == "test/a"));
        Assert.Equal(15, results.Count(r => r.Deployment == "test/b"));
        Assert.Equal(9_000, (await tracker.GetStatusAsync()).Single(s => s.Name == "a").ReservedTokens);
    }
}
