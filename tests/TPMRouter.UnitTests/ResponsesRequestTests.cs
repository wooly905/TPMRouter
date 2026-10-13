using System.Text.Json.Nodes;
using TPMRouter.Api.Relay;

namespace TPMRouter.UnitTests;

public sealed class ResponsesRequestTests
{
    [Theory]
    [InlineData("{}", "model")]
    [InlineData("{\"model\":\"auto\"}", "input")]
    [InlineData("{\"model\":\"auto\",\"input\":[]}", "input")]
    [InlineData("{\"model\":\"auto\",\"input\":[null]}", "input[0]")]
    [InlineData("{\"model\":\"auto\",\"input\":\"Hi\",\"stream\":1}", "stream")]
    [InlineData("{\"model\":\"auto\",\"input\":\"Hi\",\"max_output_tokens\":0}", "max_output_tokens")]
    [InlineData("{\"model\":\"auto\",\"input\":\"Hi\",\"max_output_tokens\":1.5}", "max_output_tokens")]
    [InlineData("{\"model\":\"auto\",\"input\":\"Hi\",\"previous_response_id\":\"resp-1\"}", "previous_response_id")]
    [InlineData("{\"model\":\"auto\",\"input\":\"Hi\",\"conversation\":\"conv-1\"}", "conversation")]
    [InlineData("{\"model\":\"auto\",\"input\":\"Hi\",\"background\":true}", "background")]
    [InlineData("{\"model\":\"auto\",\"input\":\"Hi\",\"reasoning_effort\":\"medium\"}", "reasoning_effort")]
    [InlineData("{\"model\":\"auto\",\"input\":\"Hi\",\"messages\":[]}", "messages")]
    [InlineData("{\"model\":\"auto\",\"input\":\"Hi\",\"max_completion_tokens\":10}", "max_completion_tokens")]
    [InlineData("{\"model\":\"auto\",\"input\":[{\"type\":\"item_reference\",\"id\":\"msg-1\"}]}", "input[0].type")]
    [InlineData("{\"model\":\"auto\",\"input\":[{\"role\":\"user\",\"content\":[{\"type\":\"input_image\",\"file_id\":\"file-1\"}]}]}", "input[0].content")]
    [InlineData("{\"model\":\"auto\",\"input\":[{\"role\":\"user\",\"content\":[{\"type\":\"input_image\",\"image_url\":\"http://example.invalid/image.png\"}]}]}", "input[0].content")]
    [InlineData("{\"model\":\"auto\",\"input\":[{\"role\":\"user\",\"content\":[{\"type\":\"input_image\",\"image_url\":\"https://example.invalid/image.png\"}]}]}", "input[0].content")]
    [InlineData("{\"model\":\"auto\",\"input\":[{\"role\":\"user\",\"content\":[{\"type\":\"input_file\",\"file_id\":\"file-1\"}]}]}", "input[0].content")]
    public void Validate_InvalidOrUnroutableRequest_Returns400(string body, string param)
    {
        RelayOutcome? outcome = ResponsesRequest.Validate(JsonNode.Parse(body), out ResponsesRequest? request);

        Assert.Equal(400, outcome?.StatusCode);
        Assert.Equal(param, outcome?.ErrorParam);
        Assert.Null(request);
    }

    [Fact]
    public void Validate_ImageDataUrlPart_IsAccepted()
    {
        const string url = "data:image/jpeg;base64,/9j/AAAA";
        JsonObject body = new()
        {
            ["model"] = "auto",
            ["input"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = "Describe." },
                                            new JsonObject { ["type"] = "input_image", ["image_url"] = url }),
            }),
        };

        Assert.Null(ResponsesRequest.Validate(body, out ResponsesRequest? request));
        Assert.True(request!.HasImages);
    }

    [Fact]
    public void Validate_FunctionCallHistoryAndReasoning_PreservesInput()
    {
        JsonNode body = JsonNode.Parse("""
            {"model":"auto","input":[{"role":"user","content":"Find it"},
             {"type":"reasoning","id":"rs-1","summary":[],"encrypted_content":"opaque"},
             {"type":"function_call","call_id":"call-1","name":"lookup","arguments":"{}"},
             {"type":"function_call_output","call_id":"call-1","output":"result"}],
             "reasoning":{"effort":"medium"},"stream":true,"max_output_tokens":50,"store":false}
            """)!;

        RelayOutcome? outcome = ResponsesRequest.Validate(body, out ResponsesRequest? request);

        Assert.Null(outcome);
        Assert.Same(body, request!.Body);
        Assert.Equal("auto", request.Model);
        Assert.True(request.Stream);
        Assert.Equal(50, request.MaxOutputTokens);
        Assert.False(request.HasImages);
    }
}
