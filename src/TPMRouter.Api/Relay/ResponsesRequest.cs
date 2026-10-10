using System.Text.Json.Nodes;

namespace TPMRouter.Api.Relay;

public sealed record ResponsesRequest(JsonObject Body, string Model, bool Stream, int? MaxOutputTokens)
{
    public static RelayOutcome? Validate(JsonNode? body, out ResponsesRequest? request)
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

        foreach (string field in new[] { "previous_response_id", "conversation" })
        {
            if (json[field] is not null)
            {
                return Invalid("Send the full conversation in 'input' so it can be routed across endpoints and counted in the token budget.",
                               field, "unsupported_value");
            }
        }

        foreach (string field in new[] { "messages", "max_tokens", "max_completion_tokens", "reasoning_effort", "n" })
        {
            if (json[field] is not null)
            {
                return Invalid("Use Responses API fields: 'input', 'max_output_tokens', and 'reasoning.effort'.",
                               field, "unsupported_value");
            }
        }

        if (!TryString(json["input"], out _) && json["input"] is not JsonArray { Count: > 0 })
        {
            return Invalid("The 'input' field must be a text string or a non-empty array of input items.", "input");
        }

        if (json["input"] is JsonArray items)
        {
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] is not JsonObject item)
                {
                    return Invalid("Each input item must be a JSON object.", $"input[{i}]");
                }

                if (TryString(item["type"], out string? itemType) && itemType == "item_reference")
                {
                    return Invalid("Send complete input items instead of references to stored items.",
                                   $"input[{i}].type", "unsupported_value");
                }

                if (item["content"] is JsonArray parts)
                {
                    foreach (JsonNode? part in parts)
                    {
                        if (part is not JsonObject p || !TryString(p["type"], out string? type)
                            || type is not ("input_text" or "output_text" or "refusal"))
                        {
                            return Invalid("Only text content parts are supported.", $"input[{i}].content", "unsupported_value");
                        }
                    }
                }
            }
        }

        bool stream = false;
        if (json["stream"] is not null && (json["stream"] is not JsonValue streamValue || !streamValue.TryGetValue(out stream)))
        {
            return Invalid("The 'stream' field must be a boolean.", "stream");
        }

        if (json["background"] is not null
            && (json["background"] is not JsonValue backgroundValue || !backgroundValue.TryGetValue(out bool background) || background))
        {
            return Invalid("Background responses are not supported. Use a synchronous or streaming response.", "background", "unsupported_value");
        }

        int? maxOutputTokens = null;
        if (json["max_output_tokens"] is { } limitNode)
        {
            if (limitNode is not JsonValue limitValue || !limitValue.TryGetValue(out int limit) || limit <= 0)
            {
                return Invalid("The 'max_output_tokens' field must be a positive integer.", "max_output_tokens");
            }

            maxOutputTokens = limit;
        }

        request = new ResponsesRequest(json, model!, stream, maxOutputTokens);
        return null;
    }

    private static bool TryString(JsonNode? node, out string? value)
    {
        value = null;
        return node is JsonValue json && json.TryGetValue(out value);
    }

    private static RelayOutcome Invalid(string message, string? param, string code = "invalid_value") =>
        new(StatusCodes.Status400BadRequest, Error: message, ErrorParam: param, ErrorCode: code);
}
