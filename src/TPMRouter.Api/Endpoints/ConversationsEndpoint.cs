using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TPMRouter.Api.Auth;
using TPMRouter.Api.Relay;

namespace TPMRouter.Api.Endpoints;

public static class ConversationsEndpoint
{
    public static void MapConversations(this WebApplication app)
    {
        app.MapPost("/v1/conversations", HandleAsync)
            .WithMetadata(new ApiKeyRequirement(ApiKeyScope.Client, OpenAICompatible: true));
        app.MapMethods("/v1/conversations/{conversationId}", ["GET", "POST", "DELETE"], HandleAsync)
            .WithMetadata(new ApiKeyRequirement(ApiKeyScope.Client, OpenAICompatible: true));
        app.MapMethods("/v1/conversations/{conversationId}/items", ["GET", "POST"], HandleAsync)
            .WithMetadata(new ApiKeyRequirement(ApiKeyScope.Client, OpenAICompatible: true));
        app.MapMethods("/v1/conversations/{conversationId}/items/{itemId}", ["GET", "DELETE"], HandleAsync)
            .WithMetadata(new ApiKeyRequirement(ApiKeyScope.Client, OpenAICompatible: true));
    }

    private static async Task<IResult> HandleAsync(HttpContext context, ConversationService service)
    {
        JsonObject? body = null;
        if (HttpMethods.IsPost(context.Request.Method))
        {
            if (!context.Request.HasJsonContentType())
                return OpenAIError.Result(new(400, Error: "Content-Type must be application/json."));
            try
            {
                body = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted) as JsonObject;
                if (body is null) return OpenAIError.Result(new(400, Error: "The request body must be a JSON object."));
            }
            catch (JsonException)
            {
                return OpenAIError.Result(new(400, Error: "The request body is not valid JSON."));
            }

            // Apply the same input restrictions as Responses before state reaches the resource.
            if (body["items"] is JsonArray { Count: > 0 } items)
            {
                RelayOutcome? invalid = ResponsesRequest.Validate(new JsonObject
                {
                    ["model"] = "auto", ["input"] = items.DeepClone(),
                }, out _);
                if (invalid is not null)
                    return OpenAIError.Result(invalid with { ErrorParam = invalid.ErrorParam?.Replace("input", "items") });
            }
        }

        string? id = context.Request.RouteValues["conversationId"] as string;
        string suffix = context.Request.Path.Value!.Contains("/items", StringComparison.Ordinal) ? "/items" : "";
        if (context.Request.RouteValues["itemId"] is string itemId)
        {
            if (itemId.Length > 256 || !itemId.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
                return OpenAIError.Result(new(400, Error: "The item ID is invalid.", ErrorParam: "item_id", ErrorCode: "invalid_value"));
            suffix += "/" + Uri.EscapeDataString(itemId);
        }
        // Forward only the documented Conversations API query parameters.
        QueryString query = QueryString.Create(context.Request.Query
            .Where(p => p.Key is "after" or "limit" or "order" or "include" or "include[]")
            .SelectMany(p => p.Value.Select(v => KeyValuePair.Create(p.Key, v))));
        RelayOutcome outcome = await service.ManageAsync(new HttpMethod(context.Request.Method), id, suffix,
            query.Value ?? "", body, context.RequestAborted);
        if (outcome.RetryAfter is { } retry)
            context.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retry.TotalSeconds))
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
        return outcome.StatusCode is >= 200 and < 300 && outcome.Content is { } content
            ? Results.Content(content.ToString(), "application/json", Encoding.UTF8, outcome.StatusCode)
            : OpenAIError.Result(outcome);
    }
}
