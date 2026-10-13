using TPMRouter.Api.Budget;
using System.Text.Json.Nodes;

namespace TPMRouter.UnitTests;

public class TokenEstimatorTests
{
    private readonly TokenEstimator _estimator = new();

    [Fact]
    public void CountPromptTokens_IncludesResponsesHistoryInstructionsToolsAndTextSchema()
    {
        JsonObject request = new() { ["input"] = "Hi" };
        int initial = _estimator.CountPromptTokens(request);
        request["input"] = JsonNode.Parse("""[{"role":"user","content":"Hi"},{"type":"function_call_output","call_id":"call-1","output":"a tool result"}]""");
        int withHistory = _estimator.CountPromptTokens(request);
        request["instructions"] = "Be helpful.";
        int withInstructions = _estimator.CountPromptTokens(request);
        request["tools"] = JsonNode.Parse("""[{"type":"function","name":"lookup","parameters":{"type":"object"}}]""");
        int withTools = _estimator.CountPromptTokens(request);
        request["text"] = JsonNode.Parse("""{"format":{"type":"json_schema","name":"answer","schema":{"type":"object"}}}""");

        Assert.True(withHistory > initial);
        Assert.True(withInstructions > withHistory);
        Assert.True(withTools > withInstructions);
        Assert.True(_estimator.CountPromptTokens(request) > withTools);
    }

    [Fact]
    public void CountPromptTokens_ExcludesRoutingAndGenerationParameters()
    {
        JsonObject request = new() { ["input"] = "Hi" };
        int initial = _estimator.CountPromptTokens(request);
        request["model"] = "auto";
        request["max_output_tokens"] = 500;
        request["temperature"] = 0.5;
        request["reasoning"] = JsonNode.Parse("""{"effort":"medium"}""");

        Assert.Equal(initial, _estimator.CountPromptTokens(request));
    }
}
