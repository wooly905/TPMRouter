using System.Text.Json.Nodes;
using TPMRouter.Api.Usage;

namespace TPMRouter.UnitTests;

public sealed class UsageRecordBuilderTests
{
    [Fact]
    public void ChatCompletion_CollectsConversationToolDefinitionsCallsAndActualUsage()
    {
        UsageRecordBuilder builder = new("""
            {"messages":[{"role":"system","content":"Be concise."},
                         {"role":"user","content":[{"type":"text","text":"台北天氣？"}]}],
             "tools":[{"type":"function","function":{"name":"get_weather","parameters":{"type":"object"}}}]}
            """);
        builder.ReadResponse("""
            {"choices":[{"index":0,"message":{"content":"查詢中", "tool_calls":[
                {"id":"call-1","type":"function","function":{"name":"get_weather","arguments":"{\"city\":\"Taipei\"}"}}]}}],
             "usage":{"prompt_tokens":42,"completion_tokens":12}}
            """, false);

        UsageRecord record = builder.Build(DateTimeOffset.UnixEpoch, "192.0.2.1");

        Assert.Equal("Be concise.\n台北天氣？", record.SourceText);
        Assert.Equal("get_weather", record.ToolsList[0]!["function"]!["name"]!.GetValue<string>());
        Assert.NotNull(record.ToolsList[0]!["function"]!["parameters"]);
        Assert.Equal("get_weather", record.InvokedTool[0]!["name"]!.GetValue<string>());
        Assert.Equal("{\"city\":\"Taipei\"}", record.InvokedTool[0]!["arguments"]!.GetValue<string>());
        Assert.Equal(42, record.InputTokenSize);
        Assert.Equal(12, record.OutputTokenSize);
        Assert.Equal("查詢中", record.OutputText);
        Assert.Equal("192.0.2.1", record.SourceIp);
    }

    [Fact]
    public void ChatStream_MergesToolFragmentsAcrossChoicesAndReadsFinalUsage()
    {
        UsageRecordBuilder builder = new("""{"messages":[{"role":"user","content":"Hi"}]}""");
        builder.ReadResponse("""
            data: {"choices":[{"index":0,"delta":{"content":"你", "tool_calls":[{"index":0,"function":{"name":"get_","arguments":"{\"city\":"}}]}}]}

            data: {"choices":[{"index":1,"delta":{"content":"第二個回應", "tool_calls":[{"index":0,"function":{"name":"other","arguments":"{}"}}]}}]}

            data: {"choices":[{"index":0,"delta":{"content":"好", "tool_calls":[{"index":0,"function":{"name":"weather","arguments":"\"Taipei\"}"}}]}}]}

            data: {"choices":[],"usage":{"prompt_tokens":22,"completion_tokens":8}}

            data: [DONE]

            """, true);

        UsageRecord record = builder.Build(DateTimeOffset.UnixEpoch, "");

        Assert.Equal("你好\n第二個回應", record.OutputText);
        Assert.Equal(2, record.InvokedTool.Count);
        Assert.Equal("get_weather", record.InvokedTool[0]!["name"]!.GetValue<string>());
        Assert.Equal("{\"city\":\"Taipei\"}", record.InvokedTool[0]!["arguments"]!.GetValue<string>());
        Assert.Equal("other", record.InvokedTool[1]!["name"]!.GetValue<string>());
        Assert.Equal(22, record.InputTokenSize);
        Assert.Equal(8, record.OutputTokenSize);
    }

    [Fact]
    public void ResponsesStream_TerminalOutputReplacesDeltasWithoutDuplication()
    {
        UsageRecordBuilder builder = new("""
            {"instructions":"Use tools.","input":[{"role":"user","content":[{"type":"input_text","text":"Hi"}]},
                {"type":"function_call_output","call_id":"previous","output":"sunny"}],
             "tools":[{"type":"function","name":"search"}]}
            """);
        builder.ReadResponse("""
            event: response.output_text.delta
            data: {"type":"response.output_text.delta","output_index":0,"delta":"hello"}

            event: response.output_item.added
            data: {"type":"response.output_item.added","output_index":1,"item":{"type":"function_call","name":"search","arguments":""}}

            event: response.function_call_arguments.delta
            data: {"type":"response.function_call_arguments.delta","output_index":1,"delta":"{}"}

            event: response.completed
            data: {"type":"response.completed","response":{"output":[{"type":"message","content":[{"type":"output_text","text":"hello"}]},{"type":"function_call","name":"search","arguments":"{}","call_id":"call-1"}],"usage":{"input_tokens":17,"output_tokens":9}}}

            """, true);

        UsageRecord record = builder.Build(DateTimeOffset.UnixEpoch, "");

        Assert.Equal("Use tools.\nHi\nsunny", record.SourceText);
        Assert.Equal("hello", record.OutputText);
        Assert.Single(record.InvokedTool);
        Assert.Equal("{}", record.InvokedTool[0]!["arguments"]!.GetValue<string>());
        Assert.Equal(17, record.InputTokenSize);
        Assert.Equal(9, record.OutputTokenSize);
    }

    [Fact]
    public void InterruptedResponsesStream_PreservesTextAndToolArgumentsWithUnknownUsage()
    {
        UsageRecordBuilder builder = new("""{"input":"Hello"}""");
        builder.ReadResponse("""
            data: {"type":"response.output_text.delta","output_index":0,"delta":"partial"}

            data: {"type":"response.output_item.added","output_index":1,"item":{"type":"function_call","name":"search","arguments":""}}

            data: {"type":"response.function_call_arguments.delta","output_index":1,"delta":"{\"q\":\"hello\"}"}

            data: {"type":"response.output_text.delta","delta":
            """, true);

        UsageRecord record = builder.Build(DateTimeOffset.UnixEpoch, "");

        Assert.Equal("partial", record.OutputText);
        Assert.Equal("{\"q\":\"hello\"}", record.InvokedTool[0]!["arguments"]!.GetValue<string>());
        Assert.Null(record.InputTokenSize);
        Assert.Null(record.OutputTokenSize);
    }

    [Theory]
    [InlineData("{\"input\":\"Hi\"}", "{\"error\":{\"message\":\"limited\"}}", "Hi", "limited")]
    [InlineData("not-json", "not-json", "not-json", "not-json")]
    public void ErrorsAndMalformedBodies_RecordAvailableText(string request, string response, string source, string output)
    {
        UsageRecordBuilder builder = new(request);
        builder.ReadResponse(response, false);

        UsageRecord record = builder.Build(DateTimeOffset.UnixEpoch, "");

        Assert.Equal(source, record.SourceText);
        Assert.Equal(output, record.OutputText);
        Assert.Empty(record.ToolsList);
        Assert.Empty(record.InvokedTool);
        Assert.Null(record.InputTokenSize);
    }

    [Fact]
    public void FailedResponsesStream_PreservesErrorEvenWhenOutputIsEmpty()
    {
        UsageRecordBuilder builder = new("""{"input":"Hi"}""");
        builder.ReadResponse("""
            data: {"type":"response.failed","response":{"output":[],"error":{"message":"upstream failed"}}}

            """, true);

        Assert.Equal("upstream failed", builder.Build(DateTimeOffset.UnixEpoch, "").OutputText);
    }

    [Fact]
    public void Responses_CollectsRefusalAndZeroUsage()
    {
        UsageRecordBuilder builder = new("""{"input":"Hi"}""");
        builder.ReadResponse("""
            {"output":[{"type":"message","content":[{"type":"refusal","refusal":"Cannot help."}]}],
             "usage":{"input_tokens":1,"output_tokens":0}}
            """, false);

        UsageRecord record = builder.Build(DateTimeOffset.UnixEpoch, "");

        Assert.Equal("Cannot help.", record.OutputText);
        Assert.Equal(0, record.OutputTokenSize);
    }
}
