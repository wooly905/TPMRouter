using TPMRouter.Api.Budget;
using System.Text.Json.Nodes;

namespace TPMRouter.UnitTests;

public class TokenEstimatorTests
{
    private readonly TokenEstimator _estimator = new();

    [Fact]
    public void CountPromptTokens_IncludesConversationHistory()
    {
        JsonObject request = (JsonObject)JsonNode.Parse("""{"messages":[{"role":"user","content":"Hi"}]}""")!;
        int initial = _estimator.CountPromptTokens(request);
        ((JsonArray)request["messages"]!).Insert(0, new JsonObject { ["role"] = "assistant", ["content"] = "A previous answer." });

        Assert.True(_estimator.CountPromptTokens(request) > initial);
    }

    [Fact]
    public void CountPromptTokens_IncludesToolAndOutputSchemas()
    {
        JsonObject request = (JsonObject)JsonNode.Parse("""{"messages":[{"role":"user","content":"Hi"}]}""")!;
        int initial = _estimator.CountPromptTokens(request);
        request["tools"] = JsonNode.Parse("""[{"type":"function","function":{"name":"lookup","parameters":{"type":"object"}}}]""");
        int withTools = _estimator.CountPromptTokens(request);
        request["response_format"] = JsonNode.Parse("""{"type":"json_schema","json_schema":{"name":"answer","schema":{"type":"object"}}}""");

        Assert.True(withTools > initial);
        Assert.True(_estimator.CountPromptTokens(request) > withTools);
    }

    [Fact]
    public void CountPromptTokens_ExcludesRoutingAndGenerationParameters()
    {
        JsonObject request = (JsonObject)JsonNode.Parse("""{"messages":[{"role":"user","content":"Hi"}]}""")!;
        int initial = _estimator.CountPromptTokens(request);
        request["model"] = "auto";
        request["max_completion_tokens"] = 500;
        request["temperature"] = 0.5;

        Assert.Equal(initial, _estimator.CountPromptTokens(request));
    }
}
