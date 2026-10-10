using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using TPMRouter.Api.Relay;

namespace TPMRouter.Api.Endpoints;

public sealed record OpenAIError([property: JsonPropertyName("message")] string Message,
                                 [property: JsonPropertyName("type")] string Type,
                                 [property: JsonPropertyName("param")] string? Param,
                                 [property: JsonPropertyName("code")] string? Code)
{
    public static IResult Result(RelayOutcome outcome)
    {
        string type = outcome.StatusCode switch
        {
            401 => "authentication_error",
            429 => "rate_limit_error",
            >= 500 => "server_error",
            _ => "invalid_request_error",
        };
        string? code = outcome.ErrorCode ?? outcome.StatusCode switch
        {
            401 => "invalid_api_key",
            429 => "rate_limit_exceeded",
            >= 500 => "upstream_error",
            _ => "invalid_request",
        };
        if (outcome.Content is not null && JsonNode.Parse(outcome.Content.ToString()) is JsonObject body
            && body["error"] is JsonObject error)
        {
            error.TryAdd("type", JsonValue.Create(type));
            error.TryAdd("param", null);
            error.TryAdd("code", JsonValue.Create(code));
            return Results.Json(body, statusCode: outcome.StatusCode);
        }

        return Results.Json(new { error = new OpenAIError(outcome.Error ?? "The request failed.", type, outcome.ErrorParam, code) },
                            statusCode: outcome.StatusCode);
    }
}
