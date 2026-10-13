using Microsoft.AspNetCore.Http;
using System.Text.Json.Nodes;
using TPMRouter.Api.Relay;

namespace TPMRouter.UnitTests;

public class RelayServiceTests
{
    [Fact]
    public async Task RelayResponseAsync_EstimateAboveAllDeployments_Returns413WithoutWaitOrUpstreamCall()
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
    public void Validate_MissingInput_Returns400()
    {
        RelayOutcome? outcome = ResponsesRequest.Validate(new JsonObject { ["model"] = "auto" }, out _);

        Assert.Equal(StatusCodes.Status400BadRequest, outcome?.StatusCode);
    }

    [Theory]
    [InlineData('a', 110_000)]
    [InlineData('你', 17_000)]
    public async Task RelayResponseAsync_LargeJsonWithEnoughTokenBudget_Returns200(char character, int length)
    {
        var options = TestData.Options(0.9, TestData.Deployment("a", 1_000_000, 0));
        var upstream = new FakeUpstreamClient(_ => FakeUpstreamClient.Ok());
        RelayService service = TestData.Service(options, upstream);
        string text = new(character, length);
        Assert.True(new JsonObject { ["input"] = text }.ToJsonString().Length > 100_000);

        RelayOutcome outcome = await RelayAsync(service, text);

        Assert.Equal(StatusCodes.Status200OK, outcome.StatusCode);
        Assert.Equal(["test/a"], upstream.Calls);
    }

    [Fact]
    public async Task RelayResponseAsync_SkipsDeploymentWhoseEstimateIsTooLarge()
    {
        var options = TestData.Options(0.9, TestData.Deployment("small", 100, 0, maxOutputTokens: 100, priority: 1), TestData.Deployment("big", 10_000, 0, maxOutputTokens: 100, priority: 2));
        var upstream = new FakeUpstreamClient(_ => FakeUpstreamClient.Ok());
        RelayService service = TestData.Service(options, upstream);

        RelayOutcome outcome = await RelayAsync(service, "Hello, world!");

        Assert.Equal(StatusCodes.Status200OK, outcome.StatusCode);
        Assert.Equal(["test/big"], upstream.Calls);
    }

    [Fact]
    public async Task RelayResponseAsync_Upstream500_SendsToOtherDeployment()
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
    public async Task RelayResponseAsync_Upstream400_ReturnsErrorWithoutRetry()
    {
        var options = TestData.Options(0.9, TestData.Deployment("a", 1000, 0, priority: 1), TestData.Deployment("b", 1000, 0, priority: 2));
        var upstream = new FakeUpstreamClient(_ => new(400, "bad request", default));
        RelayService service = TestData.Service(options, upstream);

        RelayOutcome outcome = await RelayAsync(service, "hi");

        Assert.Equal(StatusCodes.Status400BadRequest, outcome.StatusCode);
        Assert.Equal("bad request", outcome.Error);
        Assert.Single(upstream.Calls);
    }

    [Fact]
    public async Task RelayResponseAsync_AutoImageRequest_SkipsDeploymentsWithoutVision()
    {
        var options = TestData.Options(0.9, TestData.Deployment("text", 100_000, 0, priority: 1),
                                       TestData.Deployment("vision", 100_000, 0, priority: 2, supportsVision: true));
        var upstream = new FakeUpstreamClient(_ => FakeUpstreamClient.Ok());
        RelayService service = TestData.Service(options, upstream);

        RelayOutcome outcome = await RelayImageAsync(service, "auto");

        Assert.Equal(StatusCodes.Status200OK, outcome.StatusCode);
        Assert.Equal(["test/vision"], upstream.Calls);
    }

    [Fact]
    public async Task RelayResponseAsync_ImageRequestForModelWithoutVision_Returns400WithoutUpstreamCall()
    {
        var options = TestData.Options(0.9, TestData.Deployment("text", 100_000, 0, priority: 1),
                                       TestData.Deployment("vision", 100_000, 0, priority: 2, supportsVision: true));
        var upstream = new FakeUpstreamClient(_ => FakeUpstreamClient.Ok());
        RelayService service = TestData.Service(options, upstream);

        RelayOutcome outcome = await RelayImageAsync(service, "text");

        Assert.Equal(StatusCodes.Status400BadRequest, outcome.StatusCode);
        Assert.Equal("model", outcome.ErrorParam);
        Assert.Empty(upstream.Calls);
    }

    private static Task<RelayOutcome> RelayImageAsync(RelayService service, string model)
    {
        RelayOutcome? invalid = ResponsesRequest.Validate(JsonNode.Parse($$"""
            {"model":"{{model}}","input":[{"role":"user","content":[
                {"type":"input_text","text":"Describe."},{"type":"input_image","image_url":"data:image/jpeg;base64,/9j/AAAA"}]}]}
            """), out ResponsesRequest? request);
        Assert.Null(invalid);
        return service.RelayResponseAsync(request!, (_, _) => Task.CompletedTask, () => false, CancellationToken.None);
    }

    private static Task<RelayOutcome> RelayAsync(RelayService service, string text)
    {
        RelayOutcome? invalid = ResponsesRequest.Validate(new JsonObject
        {
            ["model"] = "auto",
            ["input"] = text,
        }, out ResponsesRequest? request);
        Assert.Null(invalid);
        return service.RelayResponseAsync(request!, (_, _) => Task.CompletedTask, () => false, CancellationToken.None);
    }
}
