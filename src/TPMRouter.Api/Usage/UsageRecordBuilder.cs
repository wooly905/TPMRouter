using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TPMRouter.Api.Usage;

public sealed class UsageRecordBuilder
{
    private readonly string _sourceText;
    private readonly JsonArray _tools;
    private readonly Dictionary<string, string> _text = [];
    private readonly Dictionary<string, JsonObject> _invoked = [];
    private long? _inputTokens;
    private long? _outputTokens;

    public UsageRecordBuilder(string request)
    {
        JsonObject? body = ParseObject(request);
        _sourceText = body is null ? request : JoinText(body["instructions"], body["messages"], body["input"]);
        _tools = (JsonArray?)((body?["tools"] as JsonArray ?? body?["functions"] as JsonArray)?.DeepClone()) ?? [];
    }

    public void ReadResponse(string response, bool streaming)
    {
        if (!streaming)
        {
            JsonObject? body = ParseObject(response);
            if (body is null)
            {
                _text["0"] = response;
                return;
            }

            ReadObject(body);
            return;
        }

        using StringReader reader = new(response);
        StringBuilder data = new();
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                data.AppendLine(line[5..].TrimStart());
            }
            else if (line.Length == 0)
            {
                ReadEvent(data.ToString());
                data.Clear();
            }
        }

        ReadEvent(data.ToString());
    }

    public UsageRecord Build(DateTimeOffset timestamp, string sourceIp) =>
        new(timestamp,
            sourceIp,
            _sourceText,
            _tools,
            new JsonArray(_invoked.Values.Select(tool => (JsonNode)tool.DeepClone()).ToArray()),
            _inputTokens,
            _outputTokens,
            string.Join("\n", _text.Values));

    private void ReadEvent(string data)
    {
        if (ParseObject(data) is { } body)
        {
            ReadObject(body);
        }
    }

    private void ReadObject(JsonObject body)
    {
        JsonObject? response = body["response"] as JsonObject;
        JsonObject payload = response ?? body;

        if (payload["usage"] is JsonObject usage)
        {
            _inputTokens = ReadTokenCount(usage["input_tokens"] ?? usage["prompt_tokens"]) ?? _inputTokens;
            _outputTokens = ReadTokenCount(usage["output_tokens"] ?? usage["completion_tokens"]) ?? _outputTokens;
        }

        if (payload["choices"] is JsonArray choices)
        {
            foreach (JsonObject choice in choices.OfType<JsonObject>())
            {
                string key = choice["index"]?.ToJsonString() ?? "0";
                JsonObject? message = (choice["message"] ?? choice["delta"]) as JsonObject;

                if (message is null)
                {
                    continue;
                }

                _text[key] = _text.GetValueOrDefault(key, "") + JoinText(message["content"], message["refusal"]);

                if (message["tool_calls"] is JsonArray calls)
                {
                    foreach (JsonObject call in calls.OfType<JsonObject>())
                    {
                        string toolKey = $"{key}:{call["index"]?.ToJsonString() ?? GetString(call["id"])}";
                        ReadChatTool(toolKey, call["function"] as JsonObject);
                    }
                }

                if (message["function_call"] is JsonObject function)
                {
                    ReadChatTool($"{key}:function", function);
                }
            }
        }

        if (payload["output"] is JsonArray output)
        {
            // Terminal Responses events include the complete output, replacing accumulated deltas.
            _text.Clear();
            _invoked.Clear();

            for (int i = 0; i < output.Count; i++)
            {
                if (output[i] is JsonObject item) ReadOutputItem(i.ToString(), item);
            }
        }

        string outputKey = body["output_index"]?.ToJsonString() ?? "0";
        string type = GetString(body["type"]);

        if (type is "response.output_text.delta" or "response.refusal.delta")
        {
            _text[outputKey] = _text.GetValueOrDefault(outputKey, "") + GetString(body["delta"]);
        }
        else if (type is "response.output_item.added" or "response.output_item.done"
                 && body["item"] is JsonObject item)
        {
            ReadOutputItem(outputKey, item);
        }
        else if (type is "response.function_call_arguments.delta" or "response.function_call_arguments.done"
                 && _invoked.TryGetValue(outputKey, out JsonObject? tool))
        {
            tool["arguments"] = type.EndsWith(".done", StringComparison.Ordinal)
                ? GetString(body["arguments"])
                : GetString(tool["arguments"]) + GetString(body["delta"]);
        }

        if (payload["error"] is JsonObject error)
        {
            _text["error"] = GetString(error["message"]);
        }
        else if (type == "error")
        {
            _text["error"] = GetString(body["message"]);
        }
    }

    private void ReadOutputItem(string key, JsonObject item)
    {
        string type = GetString(item["type"]);

        if (type == "message")
        {
            string text = JoinText(item["content"]);

            if (text.Length > 0)
            {
                _text[key] = text;
            }

        }
        else if (type.EndsWith("_call", StringComparison.Ordinal))
        {
            _invoked[key] = (JsonObject)item.DeepClone();
        }
    }

    private void ReadChatTool(string key, JsonObject? function)
    {
        if (function is null)
        {
            return;
        }

        if (!_invoked.TryGetValue(key, out JsonObject? tool))
        {
            tool = new JsonObject { ["type"] = "function_call", ["name"] = "", ["arguments"] = "" };
            _invoked[key] = tool;
        }

        tool["name"] = GetString(tool["name"]) + GetString(function["name"]);
        tool["arguments"] = GetString(tool["arguments"]) + GetString(function["arguments"]);
    }

    private static string JoinText(params JsonNode?[] nodes) => string.Join("\n", nodes.SelectMany(ReadText));

    private static IEnumerable<string> ReadText(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue(out string? text))
        {
            yield return text;
        }
        else if (node is JsonArray array)
        {
            foreach (string part in array.SelectMany(ReadText)) yield return part;
        }
        else if (node is JsonObject item)
        {
            foreach (string field in new[] { "content", "text", "refusal", "arguments", "output" })
            {
                foreach (string part in ReadText(item[field])) yield return part;
            }
        }
    }

    private static JsonObject? ParseObject(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "[DONE]")
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            // Invalid requests and interrupted SSE events must not prevent recording the available data.
            return null;
        }
    }

    private static string GetString(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text : "";

    private static long? ReadTokenCount(JsonNode? node) => node is JsonValue value && value.TryGetValue(out long count) ? count : null;
}
