using Microsoft.ML.Tokenizers;
using System.Text.Json.Nodes;

namespace TPMRouter.Api.Budget;

public sealed class TokenEstimator
{
    // Image tokens depend on pixel size, not encoded bytes, so each image gets a fixed allowance.
    public const int ImageTokens = 2000;

    private readonly Tokenizer _tokenizer = TiktokenTokenizer.CreateForEncoding("o200k_base");

    public int CountPromptTokens(JsonObject request)
    {
        // Include input framing, tool definitions, and structured output schemas in the conservative estimate.
        int count = 3;
        foreach (string field in new[] { "input", "instructions", "tools", "text" })
        {
            if (request[field] is { } value)
            {
                if (field == "input")
                {
                    value = WithoutImageData(value, out int images);
                    count += images * ImageTokens;
                }

                count += _tokenizer.CountTokens(value.ToJsonString());
            }
        }

        return count;
    }

    private static JsonNode WithoutImageData(JsonNode input, out int images)
    {
        images = 0;
        if (input is not JsonArray)
        {
            return input;
        }

        JsonArray copy = (JsonArray)input.DeepClone();
        foreach (JsonObject part in copy.OfType<JsonObject>()
                     .SelectMany(item => item["content"] as JsonArray ?? [])
                     .OfType<JsonObject>())
        {
            if (part["type"] is JsonValue type && type.TryGetValue(out string? name) && name == "input_image")
            {
                part.Remove("image_url");
                images++;
            }
        }

        return copy;
    }
}
