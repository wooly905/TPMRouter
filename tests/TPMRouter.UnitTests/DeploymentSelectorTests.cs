using TPMRouter.Api.Options;
using TPMRouter.Api.Relay;

namespace TPMRouter.UnitTests;

public class DeploymentSelectorTests
{
    private readonly DeploymentSelector _selector = new();

    [Fact]
    public void Select_SkipsDeploymentAtTokenLimit()
    {
        DeploymentOptions a = TestData.Deployment("a", 1000, 0);
        DeploymentOptions b = TestData.Deployment("b", 1000, 0);

        DeploymentBudget? selected = _selector.Select(
            [new(TestData.Target(a), AvailableTokens: 99, AvailableRequests: null, InCooldown: false), new(TestData.Target(b), 100, null, false)],
            new Dictionary<string, long> { ["test/a"] = 100, ["test/b"] = 100 });

        Assert.Equal("b", selected?.Deployment.Name);
    }

    [Fact]
    public void Select_SkipsDeploymentWithFullRpmAndFreeTpm()
    {
        DeploymentOptions a = TestData.Deployment("a", 1000, 10);
        DeploymentOptions b = TestData.Deployment("b", 1000, 10);

        DeploymentBudget? selected = _selector.Select(
            [new(TestData.Target(a), 1000, AvailableRequests: 0, false), new(TestData.Target(b), 200, 5, false)],
            new Dictionary<string, long> { ["test/a"] = 100, ["test/b"] = 100 });

        Assert.Equal("b", selected?.Deployment.Name);
    }

    [Fact]
    public void Select_UsesTokenRatioWhenRpmIsZero()
    {
        DeploymentOptions noRpm = TestData.Deployment("no-rpm", 1000, 0);
        DeploymentOptions withRpm = TestData.Deployment("with-rpm", 1000, 10);

        // no-rpm ratio = 0.8 (TPM only); with-rpm ratio = min(0.9, 0.5) = 0.5.
        DeploymentBudget? selected = _selector.Select(
            [new(TestData.Target(noRpm), 800, null, false), new(TestData.Target(withRpm), 900, 5, false)],
            new Dictionary<string, long> { ["test/no-rpm"] = 100, ["test/with-rpm"] = 100 });

        Assert.Equal("no-rpm", selected?.Deployment.Name);
        Assert.Equal(0.8, DeploymentSelector.RemainingRatio(selected!), 3);
    }

    [Fact]
    public void Select_PrefersLowerPriorityValue()
    {
        DeploymentOptions preferred = TestData.Deployment("preferred", 1000, 0, priority: 1);
        DeploymentOptions other = TestData.Deployment("other", 1_000_000, 0, priority: 2);

        DeploymentBudget? selected = _selector.Select(
            [new(TestData.Target(other), 1_000_000, null, false), new(TestData.Target(preferred), 150, null, false)],
            new Dictionary<string, long> { ["test/preferred"] = 100, ["test/other"] = 100 });

        Assert.Equal("preferred", selected?.Deployment.Name);
    }

    [Fact]
    public void Select_SamePriority_PrefersHigherRemainingRatioThenAvailableTokens()
    {
        DeploymentOptions small = TestData.Deployment("small", 1000, 0);
        DeploymentOptions large = TestData.Deployment("large", 100_000, 0);
        DeploymentOptions largeTwin = TestData.Deployment("large-twin", 200_000, 0);

        DeploymentBudget? selected = _selector.Select(
            [new(TestData.Target(small), 900, null, false), new(TestData.Target(large), 50_000, null, false), new(TestData.Target(largeTwin), 100_000, null, false)],
            new Dictionary<string, long> { ["test/small"] = 100, ["test/large"] = 100, ["test/large-twin"] = 100 });

        Assert.Equal("small", selected?.Deployment.Name);

        selected = _selector.Select(
            [new(TestData.Target(large), 50_000, null, false), new(TestData.Target(largeTwin), 100_000, null, false)],
            new Dictionary<string, long> { ["test/large"] = 100, ["test/large-twin"] = 100 });

        Assert.Equal("large-twin", selected?.Deployment.Name);
    }

    [Fact]
    public void Select_SkipsDeploymentInCooldown()
    {
        DeploymentOptions a = TestData.Deployment("a", 1000, 0);

        DeploymentBudget? selected = _selector.Select(
            [new(TestData.Target(a), 1000, null, InCooldown: true)],
            new Dictionary<string, long> { ["test/a"] = 100 });

        Assert.Null(selected);
    }
}
