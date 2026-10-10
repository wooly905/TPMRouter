using Microsoft.AspNetCore.Http;
using System.Text.Json.Nodes;
using TPMRouter.Api.Relay;

namespace TPMRouter.UnitTests;

public class RelayServiceTests
{
    [Fact]
    public async Task RelayChatAsync_EstimateAboveAllDeployments_Returns413WithoutWaitOrUpstreamCall()
    {
        var options = TestData.Options(0.9, TestData.Deployment("a", 1000, 0, maxOutputTokens: 900), TestData.Deployment("b", 2000, 0, maxOutputTokens: 1800));
        var upstream = new FakeUpstreamClient(_ => FakeUpstreamClient.Ok());
        RelayService service = TestData.Service(options, upstream);

        // FakeTimeProvider never advances, so any wait would hang the test.
        RelayOutcome outcome = await RelayAsync(service, "Hello, world!");

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, outcome.StatusCode);
        Assert.Empty(upstream.Calls);
    }

    [Fact]
    public void Validate_MissingMessages_Returns400()
    {
        RelayOutcome? outcome = ChatCompletionsRequest.Validate(new JsonObject { ["model"] = "auto" }, out _);

        Assert.Equal(StatusCodes.Status400BadRequest, outcome?.StatusCode);
    }

    [Fact]
    public async Task RelayChatAsync_TooManyCharacters_Returns413()
    {
        var options = TestData.Options(0.9, TestData.Deployment("a", 1000, 0));
        options.MaxInputCharacters = 10;
        RelayService service = TestData.Service(options, new FakeUpstreamClient(_ => FakeUpstreamClient.Ok()));

        RelayOutcome outcome = await RelayAsync(service, "123456", "12345");

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, outcome.StatusCode);
    }

    [Fact]
    public async Task RelayChatAsync_SkipsDeploymentWhoseEstimateIsTooLarge()
    {
        var options = TestData.Options(0.9, TestData.Deployment("small", 100, 0, maxOutputTokens: 100, priority: 1), TestData.Deployment("big", 10_000, 0, maxOutputTokens: 100, priority: 2));
        var upstream = new FakeUpstreamClient(_ => FakeUpstreamClient.Ok());
        RelayService service = TestData.Service(options, upstream);

        RelayOutcome outcome = await RelayAsync(service, "Hello, world!");

        Assert.Equal(StatusCodes.Status200OK, outcome.StatusCode);
        Assert.Equal(["test/big"], upstream.Calls);
    }

    [Fact]
    public async Task RelayChatAsync_Upstream500_SendsToOtherDeployment()
    {
        var options = TestData.Options(0.9, TestData.Deployment("a", 1000, 0, priority: 1), TestData.Deployment("b", 1000, 0, priority: 2));
        var upstream = new FakeUpstreamClient(d => d == "test/a"
            ? new(500, "boom", default)
            : FakeUpstreamClient.Ok());
        RelayService service = TestData.Service(options, upstream);

        RelayOutcome outcome = await RelayAsync(service, "hi");

        Assert.Equal(StatusCodes.Status200OK, outcome.StatusCode);
        Assert.Equal(["test/a", "test/b"], upstream.Calls);
    }

    [Fact]
    public async Task RelayChatAsync_Upstream400_ReturnsErrorWithoutRetry()
    {
        var options = TestData.Options(0.9, TestData.Deployment("a", 1000, 0, priority: 1), TestData.Deployment("b", 1000, 0, priority: 2));
        var upstream = new FakeUpstreamClient(_ => new(400, "bad request", default));
        RelayService service = TestData.Service(options, upstream);

        RelayOutcome outcome = await RelayAsync(service, "hi");

        Assert.Equal(StatusCodes.Status400BadRequest, outcome.StatusCode);
        Assert.Equal("bad request", outcome.Error);
        Assert.Single(upstream.Calls);
    }

    private static Task<RelayOutcome> RelayAsync(RelayService service, string text, string? systemPrompt = null)
    {
        JsonArray messages = [];
        if (systemPrompt is not null)
        {
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = systemPrompt });
        }

        messages.Add(new JsonObject { ["role"] = "user", ["content"] = text });
        RelayOutcome? invalid = ChatCompletionsRequest.Validate(new JsonObject
        {
            ["model"] = "auto",
            ["messages"] = messages,
        }, out ChatCompletionsRequest? request);
        Assert.Null(invalid);
        return service.RelayChatAsync(request!, (_, _) => Task.CompletedTask, () => false, CancellationToken.None);
    }
}
