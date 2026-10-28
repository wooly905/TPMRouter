using System.Text.Json.Nodes;
using TPMRouter.Api.Relay;

namespace TPMRouter.UnitTests;

public sealed class EncryptedReasoningTests
{
    [Fact]
    public void Strip_RemovesCiphertextAndKeepsReasoningItems()
    {
        JsonObject body = (JsonObject)JsonNode.Parse("""
            {
              "include": ["reasoning.encrypted_content", "message.output_text.logprobs"],
              "input": [{"type":"reasoning","id":"rs-1","summary":[{"type":"summary_text","text":"looked"}],"encrypted_content":"secret"}],
              "output": [{"type":"reasoning","id":"rs-2","encrypted_content":"secret2","summary":[]}]
            }
            """)!;

        EncryptedReasoning.Strip(body);

        Assert.Equal("message.output_text.logprobs", body["include"]![0]!.GetValue<string>());
        Assert.Single((JsonArray)body["include"]!);
        Assert.Null(body["input"]![0]!["encrypted_content"]);
        Assert.Equal("rs-1", body["input"]![0]!["id"]!.GetValue<string>());
        Assert.Equal("looked", body["input"]![0]!["summary"]![0]!["text"]!.GetValue<string>());
        Assert.Null(body["output"]![0]!["encrypted_content"]);
        Assert.Equal("rs-2", body["output"]![0]!["id"]!.GetValue<string>());
    }

    [Fact]
    public void Strip_RemovesAnIncludeArrayThatOnlyRequestsCiphertext()
    {
        JsonObject body = new()
        {
            ["include"] = new JsonArray(EncryptedReasoning.IncludeValue),
        };

        EncryptedReasoning.Strip(body);

        Assert.Null(body["include"]);
    }

    [Fact]
    public void StripJson_LeavesPayloadsWithoutCiphertextUnchanged()
    {
        const string json = """{"type":"response.completed","response":{"id":"resp-1"}}""";

        Assert.Equal(json, EncryptedReasoning.StripJson(json));
        Assert.Equal("data: [DONE]", "data: " + EncryptedReasoning.StripJson("[DONE]"));
    }

    [Fact]
    public void StripJson_RemovesCiphertextFromAStreamEvent()
    {
        const string json = """{"type":"response.output_item.done","item":{"type":"reasoning","id":"rs-1","encrypted_content":"secret","summary":[]}}""";

        JsonObject stripped = (JsonObject)JsonNode.Parse(EncryptedReasoning.StripJson(json))!;

        Assert.Null(stripped["item"]!["encrypted_content"]);
        Assert.Equal("rs-1", stripped["item"]!["id"]!.GetValue<string>());
    }
}
