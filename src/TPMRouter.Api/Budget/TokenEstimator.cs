using Microsoft.ML.Tokenizers;
using System.Text.Json.Nodes;

namespace TPMRouter.Api.Budget;

public sealed class TokenEstimator
{
    private readonly Tokenizer _tokenizer = TiktokenTokenizer.CreateForEncoding("o200k_base");

    public int CountPromptTokens(JsonObject request)
    {
        // Include message framing, tool definitions, and structured output schemas in the conservative estimate.
        int count = 3;
        foreach (string field in new[] { "messages", "tools", "functions", "response_format" })
        {
            if (request[field] is { } value)
            {
                count += _tokenizer.CountTokens(value.ToJsonString());
            }
        }

        return count;
    }
}
