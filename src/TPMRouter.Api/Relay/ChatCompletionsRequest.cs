using System.Text.Json.Nodes;

namespace TPMRouter.Api.Relay;

public sealed record ChatCompletionsRequest(JsonObject Body, string Model, bool Stream, int? MaxOutputTokens, int Choices)
{
    public static RelayOutcome? Validate(JsonNode? body, out ChatCompletionsRequest? request)
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

        for (int i = 0; i < messages.Count; i++)
        {
            if (messages[i] is not JsonObject message || !TryString(message["role"], out string? role)
                || role is not ("system" or "developer" or "user" or "assistant" or "tool" or "function"))
            {
                return Invalid("Invalid message role.", $"messages[{i}].role");
            }

            JsonNode? content = message["content"];
            bool optionalContent = role == "function" || role == "assistant"
                && (message["tool_calls"] is JsonArray { Count: > 0 } || message["function_call"] is JsonObject
                    || TryString(message["refusal"], out _));
            if (content is JsonArray parts)
            {
                if (parts.Count == 0)
                {
                    return Invalid("Message content parts must not be empty.", $"messages[{i}].content");
                }

                foreach (JsonNode? part in parts)
                {
                    if (part is not JsonObject p || !TryString(p["type"], out string? type)
                        || !(type == "text" && TryString(p["text"], out _)
                            || role == "assistant" && type == "refusal" && TryString(p["refusal"], out _)))
                    {
                        return Invalid("Only text content parts are supported.", $"messages[{i}].content", "unsupported_value");
                    }
                }
            }
            else if (!TryString(content, out _) && !(content is null && optionalContent))
            {
                return Invalid("Message content must be text or an array of text parts.", $"messages[{i}].content");
            }

            if (message["audio"] is not null)
            {
                return Invalid("Audio messages are not supported.", $"messages[{i}].audio", "unsupported_value");
            }
        }

        if (json["audio"] is not null || (json["modalities"] is not null
            && (json["modalities"] is not JsonArray modalities || modalities.Count != 1
                || !TryString(modalities[0], out string? modality) || modality != "text")))
        {
            return Invalid("Only text output is supported.", json["audio"] is not null ? "audio" : "modalities", "unsupported_value");
        }

        bool stream = false;
        if (json["stream"] is not null && (json["stream"] is not JsonValue streamValue || !streamValue.TryGetValue(out stream)))
        {
            return Invalid("The 'stream' field must be a boolean.", "stream");
        }

        int choices = 1;
        if (json["n"] is not null && !TryPositiveInt(json["n"], out choices))
        {
            return Invalid("The 'n' field must be a positive integer.", "n");
        }

        if (json["max_tokens"] is not null && json["max_completion_tokens"] is not null)
        {
            return Invalid("Specify only one of 'max_tokens' and 'max_completion_tokens'.", "max_completion_tokens");
        }

        string tokenField = json["max_tokens"] is not null ? "max_tokens" : "max_completion_tokens";
        int? maxOutputTokens = null;
        if (json[tokenField] is not null)
        {
            if (!TryPositiveInt(json[tokenField], out int limit))
            {
                return Invalid($"The '{tokenField}' field must be a positive integer.", tokenField);
            }

            maxOutputTokens = limit;
        }

        request = new ChatCompletionsRequest(json, model!, stream, maxOutputTokens, choices);
        return null;
    }

    private static bool TryString(JsonNode? node, out string? value)
    {
        value = null;
        return node is JsonValue json && json.TryGetValue(out value);
    }

    private static bool TryPositiveInt(JsonNode? node, out int value)
    {
        value = 0;
        return node is JsonValue json && json.TryGetValue(out value) && value > 0;
    }

    private static RelayOutcome Invalid(string message, string? param, string code = "invalid_value") =>
        new(StatusCodes.Status400BadRequest, Error: message, ErrorParam: param, ErrorCode: code);
}
