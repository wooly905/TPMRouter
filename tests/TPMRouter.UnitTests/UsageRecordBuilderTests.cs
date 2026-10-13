using System.Text.Json.Nodes;
using TPMRouter.Api.Usage;

namespace TPMRouter.UnitTests;

public sealed class UsageRecordBuilderTests
{
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
