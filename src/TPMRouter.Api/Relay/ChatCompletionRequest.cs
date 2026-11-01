using System.Text.Json.Nodes;

namespace TPMRouter.Api.Relay;

public sealed record ChatCompletionRequest(JsonObject Body, string Model, bool Stream, int? MaxOutputTokens, bool HasImages)
    : RelayRequest(Body, Model, Stream, MaxOutputTokens, HasImages)
{
    public override string OutputLimitField => Body["max_tokens"] is not null ? "max_tokens" : "max_completion_tokens";
    public override int ChoiceCount => Body["n"]?.GetValue<int>() ?? 1;

    public static RelayOutcome? Validate(JsonNode? body, out RelayRequest? request)
    {
        request = null;

        if (body is not JsonObject json)
        {
            return Invalid("The request body must be a JSON object.", null);
        }

        if (!TryString(json["model"], out string? model) || string.IsNullOrWhiteSpace(model))
        {
            return Invalid("The 'model' field is required and must be a non-empty string.", "model");
        }

        if (json["messages"] is not JsonArray { Count: > 0 } messages)
        {
            return Invalid("The 'messages' field must be a non-empty array.", "messages");
        }

        bool hasImages = false;

        for (int i = 0; i < messages.Count; i++)
        {
            string field = $"messages[{i}]";

            if (messages[i] is not JsonObject message || !TryString(message["role"], out string? role)
                || role is not ("developer" or "system" or "user" or "assistant" or "tool" or "function"))
            {
                return Invalid("Each message must have a supported role.", field + ".role");
            }

            if (role == "tool" && (!TryString(message["tool_call_id"], out string? callId) || string.IsNullOrWhiteSpace(callId)))
            {
                return Invalid("Tool messages require 'tool_call_id'.", field + ".tool_call_id");
            }

            if (message["content"] is null && role == "assistant"
                && (message["tool_calls"] is JsonArray { Count: > 0 } || message["function_call"] is JsonObject
                    || TryString(message["refusal"], out _)))
            {
                continue;
            }

            if (TryString(message["content"], out _))
            {
                continue;
            }

            if (message["content"] is not JsonArray { Count: > 0 } parts)
            {
                return Invalid("Message content must be a string or a non-empty array of content parts.", field + ".content");
            }

            foreach (JsonNode? node in parts)
            {
                if (node is not JsonObject part || !TryString(part["type"], out string? type))
                {
                    return Invalid("Each content part must have a type.", field + ".content");
                }

                if (type == "text" && TryString(part["text"], out _)
                    || type == "refusal" && role == "assistant" && TryString(part["refusal"], out _))
                {
                    continue;
                }

                if (type != "image_url" || role != "user"
                    || part["image_url"] is not JsonObject image
                    || !TryString(image["url"], out string? url)
                    || !url!.StartsWith("data:image/", StringComparison.Ordinal))
                {
                    return Invalid("Only text and Base64 data URL images (data:image/...) are supported.", field + ".content");
                }

                hasImages = true;
            }
        }

        bool stream = false;

        if (json["stream"] is not null && (json["stream"] is not JsonValue streamValue || !streamValue.TryGetValue(out stream)))
        {
            return Invalid("The 'stream' field must be a boolean.", "stream");
        }

        int? maxOutputTokens = null;

        foreach (string field in new[] { "max_completion_tokens", "max_tokens", "n" })
        {
            if (json[field] is not { } node)
            {
                continue;
            }

            if (node is not JsonValue value || !value.TryGetValue(out int limit) || limit <= 0)
            {
                return Invalid($"The '{field}' field must be a positive integer.", field);
            }

            if (field != "n")
            {
                maxOutputTokens = limit;
            }
        }

        if (json["max_tokens"] is not null && json["max_completion_tokens"] is not null)
        {
            return Invalid("Specify only one of 'max_tokens' and 'max_completion_tokens'.", "max_tokens");
        }

        request = new ChatCompletionRequest(json, model!, stream, maxOutputTokens, hasImages);

        return null;
    }

    private static bool TryString(JsonNode? node, out string? value)
    {
        value = null;

        return node is JsonValue json && json.TryGetValue(out value);
    }

    private static RelayOutcome Invalid(string message, string? param) =>
        new(400, Error: message, ErrorParam: param, ErrorCode: "invalid_value");
}
