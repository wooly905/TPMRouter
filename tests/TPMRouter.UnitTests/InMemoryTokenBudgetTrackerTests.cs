using Microsoft.Extensions.Time.Testing;
using TPMRouter.Api.Budget;
using TPMRouter.Api.Options;

namespace TPMRouter.UnitTests;

public class InMemoryTokenBudgetTrackerTests
{
    private readonly FakeTimeProvider _time = new();

    [Theory]
    [InlineData("reservation")]
    [InlineData("tokens")]
    [InlineData("requests")]
    [InlineData("cooldown")]
    public void SameNamedDeployments_KeepBudgetAndCooldownSeparate(string limit)
    {
        RelayOptions options = TestData.Options(1.0, TestData.Deployment("shared", 1000, 0));
        options.Endpoints.Add(TestData.Endpoint("other", TestData.Deployment("shared", 1000, 0)));
        var tracker = TestData.Tracker(options, _time);
        switch (limit)
        {
            case "reservation":
                Assert.True(tracker.TryReserve(new Dictionary<string, long> { ["test/shared"] = 1000 }).Success);
                break;
            case "tokens":
                tracker.RecordHeaders("test/shared", 0, null, _time.GetUtcNow());
                break;
            case "requests":
                tracker.RecordHeaders("test/shared", null, 0, _time.GetUtcNow());
                break;
            case "cooldown":
                tracker.SetCooldown("test/shared", TimeSpan.FromSeconds(30));
                break;
        }

        ReservationResult result = tracker.TryReserve(new Dictionary<string, long>
        {
            ["test/shared"] = 1000,
            ["other/shared"] = 1000,
        });

        Assert.Equal("other/shared", result.Deployment);
        Assert.Equal(1000, tracker.GetStatus().Single(s => s.EndpointId == "other").ReservedTokens);
        Assert.All(tracker.GetStatus(), s => Assert.Equal("shared", s.Model));
    }

    [Fact]
    public void ReportedUsage_IsIsolatedAndExpiresFromReportTime()
    {
        RelayOptions options = TestData.Options(1.0, TestData.Deployment("shared", 1000, 0));
        options.Endpoints.Add(TestData.Endpoint("other", TestData.Deployment("shared", 1000, 0)));
        var tracker = TestData.Tracker(options, _time);
        tracker.TryReserve(new Dictionary<string, long> { ["test/shared"] = 1000 });
        _time.Advance(TimeSpan.FromSeconds(30));
        tracker.RecordUsage("test/shared", 15);
        tracker.RecordUsage("other/shared", 7);

        Assert.Equal(15, tracker.GetStatus().Single(status => status.EndpointId == "test").ActualTokens);
        Assert.Equal(7, tracker.GetStatus().Single(status => status.EndpointId == "other").ActualTokens);
        Assert.Equal(1000, tracker.GetStatus().Single(status => status.EndpointId == "test").ReservedTokens);
        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(0, tracker.GetStatus().Single(status => status.EndpointId == "test").ReservedTokens);
        Assert.Equal(15, tracker.GetStatus().Single(status => status.EndpointId == "test").ActualTokens);
        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.All(tracker.GetStatus(), status => Assert.Equal(0, status.ActualTokens));
    }

    [Fact]
    public void DeploymentKeys_WithSeparators_DoNotCollide()
    {
        DeploymentTarget first = TestData.Target(TestData.Deployment("c", 1000, 0), "a/b");
        DeploymentTarget second = TestData.Target(TestData.Deployment("b/c", 1000, 0), "a");

        Assert.NotEqual(first.Key, second.Key);
        Assert.Equal("a%2Fb/c", first.Key);
        Assert.Equal("a/b%2Fc", second.Key);
    }

    [Fact]
    public void Reservation_ExpiresAfter60Seconds()
    {
        var tracker = TestData.Tracker(TestData.Options(1.0, TestData.Deployment("a", 1000, 0)), _time);
        Dictionary<string, long> estimates = new() { ["test/a"] = 1000 };

        Assert.True(tracker.TryReserve(estimates).Success);
        ReservationResult full = tracker.TryReserve(estimates);
        Assert.False(full.Success);
        Assert.Equal(TimeSpan.FromSeconds(60), full.RetryAfter);

        _time.Advance(TimeSpan.FromSeconds(59));
        Assert.False(tracker.TryReserve(estimates).Success);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(tracker.TryReserve(estimates).Success);
    }

    [Fact]
    public void HeaderSnapshot_ReplacesLocalValue()
    {
        var tracker = TestData.Tracker(TestData.Options(1.0, TestData.Deployment("a", 1000, 0)), _time);

        tracker.RecordHeaders("test/a", remainingTokens: 300, remainingRequests: null, _time.GetUtcNow());

        Assert.Equal(300, tracker.GetStatus()[0].AvailableTokens);
        Assert.False(tracker.TryReserve(new Dictionary<string, long> { ["test/a"] = 301 }).Success);
        Assert.True(tracker.TryReserve(new Dictionary<string, long> { ["test/a"] = 300 }).Success);
    }

    [Fact]
    public void HeaderSnapshot_SubtractsReservationsAfterReceivedAt()
    {
        var tracker = TestData.Tracker(TestData.Options(1.0, TestData.Deployment("a", 1000, 0)), _time);
        tracker.RecordHeaders("test/a", remainingTokens: 500, remainingRequests: null, _time.GetUtcNow());

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(tracker.TryReserve(new Dictionary<string, long> { ["test/a"] = 200 }).Success);

        Assert.Equal(300, tracker.GetStatus()[0].AvailableTokens);
    }

    [Fact]
    public void HeaderSnapshot_OlderDoesNotReplaceNewer()
    {
        var tracker = TestData.Tracker(TestData.Options(1.0, TestData.Deployment("a", 1000, 0)), _time);
        DateTimeOffset older = _time.GetUtcNow();
        DateTimeOffset newer = older.AddSeconds(1);

        tracker.RecordHeaders("test/a", remainingTokens: 800, remainingRequests: 8, newer);
        tracker.RecordHeaders("test/a", remainingTokens: 100, remainingRequests: 1, older);

        Assert.Equal(800, tracker.GetStatus()[0].HeaderRemainingTokens);
        Assert.Equal(8, tracker.GetStatus()[0].HeaderRemainingRequests);
    }

    [Fact]
    public void HeaderSnapshot_ExpiresAfter60Seconds()
    {
        var tracker = TestData.Tracker(TestData.Options(1.0, TestData.Deployment("a", 1000, 0)), _time);
        tracker.RecordHeaders("test/a", remainingTokens: 0, remainingRequests: null, _time.GetUtcNow());
        Assert.Equal(0, tracker.GetStatus()[0].AvailableTokens);

        _time.Advance(TimeSpan.FromSeconds(60));

        Assert.Null(tracker.GetStatus()[0].HeaderRemainingTokens);
        Assert.Equal(1000, tracker.GetStatus()[0].AvailableTokens);
    }

    [Fact]
    public void Cooldown_BlocksDeploymentUntilItEnds()
    {
        var tracker = TestData.Tracker(TestData.Options(1.0, TestData.Deployment("a", 1000, 0)), _time);
        tracker.SetCooldown("test/a", TimeSpan.FromSeconds(5));

        ReservationResult blocked = tracker.TryReserve(new Dictionary<string, long> { ["test/a"] = 1 });
        Assert.False(blocked.Success);
        Assert.Equal(TimeSpan.FromSeconds(5), blocked.RetryAfter);

        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.True(tracker.TryReserve(new Dictionary<string, long> { ["test/a"] = 1 }).Success);
    }

    [Fact]
    public async Task ParallelReservations_DoNotExceedTpm()
    {
        var tracker = TestData.Tracker(TestData.Options(0.9, TestData.Deployment("a", 10_000, 0), TestData.Deployment("b", 5_000, 0)), _time);
        Dictionary<string, long> estimates = new() { ["test/a"] = 300, ["test/b"] = 300 };

        ReservationResult[] results = await Task.WhenAll(
            Enumerable.Range(0, 100).Select(_ => Task.Run(() => tracker.TryReserve(estimates))));

        long reservedA = results.Where(r => r.Deployment == "test/a").Sum(r => r.Tokens);
        long reservedB = results.Where(r => r.Deployment == "test/b").Sum(r => r.Tokens);
        Assert.True(reservedA <= 9_000, $"a reserved {reservedA}");
        Assert.True(reservedB <= 4_500, $"b reserved {reservedB}");
        Assert.Equal(30 + 15, results.Count(r => r.Success));
        Assert.Equal(reservedA, tracker.GetStatus().Single(s => s.Name == "a").ReservedTokens);
    }
}
