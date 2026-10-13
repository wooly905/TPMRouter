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
            data: {"type":"response.completed","response":{"model":"gpt-4.1","output":[{"type":"message","content":[{"type":"output_text","text":"hello"}]},{"type":"function_call","name":"search","arguments":"{}","call_id":"call-1"}],"usage":{"input_tokens":17,"output_tokens":9}}}

            """, true);

        UsageRecord record = builder.Build(DateTimeOffset.UnixEpoch, "");

        Assert.Equal("Use tools.\nHi\nsunny", record.SourceText);
        Assert.Equal("tool", record.InvokedType);
        Assert.Equal("text", record.InputType);
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
        Assert.Equal("user", record.InvokedType);
        Assert.Equal("{\"q\":\"hello\"}", record.InvokedTool[0]!["arguments"]!.GetValue<string>());
        Assert.Null(record.InputTokenSize);
        Assert.Null(record.OutputTokenSize);
    }

    [Fact]
    public void ImageInput_RecordsImageTypeWithoutImageContent()
    {
        UsageRecordBuilder builder = new("""
            {"input":[{"role":"user","content":[{"type":"input_text","text":"Read the board."},
                {"type":"input_image","image_url":"data:image/jpeg;base64,SECRETIMAGEDATA"}]}]}
            """);

        UsageRecord record = builder.Build(DateTimeOffset.UnixEpoch, "");

        Assert.Equal("image", record.InputType);
        Assert.Equal("Read the board.", record.SourceText);
        Assert.DoesNotContain("SECRETIMAGEDATA", record.SourceText);
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
            {"model":"gpt-4.1-mini","output":[{"type":"message","content":[{"type":"refusal","refusal":"Cannot help."}]}],
             "usage":{"input_tokens":1,"output_tokens":0}}
            """, false);

        UsageRecord record = builder.Build(DateTimeOffset.UnixEpoch, "");

        Assert.Equal("Cannot help.", record.OutputText);
        Assert.Equal(0, record.OutputTokenSize);
    }

    [Fact]
    public void ChatId_StaysStableAcrossTurnsAndMessageIdIsUnique()
    {
        UsageRecord first = new UsageRecordBuilder("""{"input":"Hi"}""").Build(DateTimeOffset.UnixEpoch, "10.0.0.1");
        UsageRecord next = new UsageRecordBuilder("""
            {"input":[{"role":"user","content":[{"type":"input_text","text":"Hi"}]},
                      {"role":"assistant","content":"Hello"},{"role":"user","content":"Weather?"}]}
            """).Build(DateTimeOffset.UnixEpoch, "10.0.0.1");
        UsageRecord otherUser = new UsageRecordBuilder("""{"input":"Hi"}""").Build(DateTimeOffset.UnixEpoch, "10.0.0.2");

        Assert.True(Guid.TryParse(first.ChatId, out _));
        Assert.Equal(first.ChatId, next.ChatId);
        Assert.NotEqual(first.ChatId, otherUser.ChatId);
        Assert.True(Guid.TryParse(first.MessageId, out _));
        Assert.NotEqual(first.MessageId, next.MessageId);
    }
}
