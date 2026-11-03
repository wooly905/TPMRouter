using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TPMRouter.Api.Relay;
using TPMRouter.Api.Usage;

namespace TPMRouter.Api.Endpoints;

public static class ResponsesEndpoint
{
    public static Task<IResult> HandleAsync(HttpContext context, RelayService service) => HandleRequestAsync(context, service, false);

    internal static async Task<IResult> HandleRequestAsync(HttpContext context, RelayService service, bool chatCompletion)
    {
        if (!context.Request.HasJsonContentType())
        {
            return OpenAIError.Result(new RelayOutcome(400, Error: "Content-Type must be application/json."));
        }

        JsonNode? body;
        try
        {
            body = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
        }
        catch (JsonException)
        {
            return OpenAIError.Result(new RelayOutcome(400, Error: "The request body is not valid JSON."));
        }

        RelayRequest? request;
        RelayOutcome? invalid;

        if (chatCompletion)
        {
            invalid = ChatCompletionRequest.Validate(body, out request);
        }
        else
        {
            invalid = ResponsesRequest.Validate(body, out ResponsesRequest? responsesRequest);
            request = responsesRequest;
        }

        if (invalid is not null)
        {
            return OpenAIError.Result(invalid);
        }

        string? conversationId = null;
        if (request is ResponsesRequest responses && responses.Body["conversation"] is not null)
        {
            (ResponsesRequest prepared, RelayOutcome? error) = await context.RequestServices
                .GetRequiredService<ConversationService>().PrepareAsync(responses, context.RequestAborted);
            if (error is not null)
            {
                if (error.RetryAfter is { } retry)
                    context.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retry.TotalSeconds))
                        .ToString(System.Globalization.CultureInfo.InvariantCulture);
                return OpenAIError.Result(error);
            }
            request = prepared;
            conversationId = prepared.RouterConversationId;
        }

        // Conversation requests stay on one resource and can retain its reasoning ciphertext.
        if (!chatCompletion && conversationId is null)
        {
            EncryptedReasoning.Strip(request!.Body);
        }

        UsageRecordBuilder streamedUsage = new("");
        RelayOutcome outcome = await service.RelayResponseAsync(request!,
                                                                (response, token) => chatCompletion
                                                                    ? ChatCompletionEndpoint.CopyStreamAsync(response, context, streamedUsage, token)
                                                                    : CopyStreamAsync(response, context, streamedUsage, token, conversationId),
                                                                () => context.Response.HasStarted,
                                                                context.RequestAborted,
                                                                streamedUsage,
                                                                target => context.Items[UsageHistoryMiddleware.DeploymentKey] =
                                                                    $"{target.EndpointId}/{target.Deployment.Name}");
        if (context.Response.HasStarted)
        {
            if (outcome.StatusCode != StatusCodes.Status200OK)
            {
                context.Abort();
            }

            return Results.Empty;
        }

        if (outcome.RetryAfter is { } retryAfter)
        {
            context.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return outcome.Content is not null && outcome.StatusCode is >= 200 and < 300
            ? Results.Content(chatCompletion ? outcome.Content.ToString() : ConversationService.RewriteResponse(outcome.Content.ToString(), conversationId), "application/json", Encoding.UTF8, outcome.StatusCode)
            : OpenAIError.Result(outcome);
    }

    private static async Task CopyStreamAsync(PipelineResponse response, HttpContext context,
                                               UsageRecordBuilder usage, CancellationToken token, string? conversationId)
    {
        using StreamReader reader = new(response.ContentStream!, Encoding.UTF8);
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        StringBuilder data = new();
        while (await reader.ReadLineAsync(token) is { } line)
        {
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                string payload = ConversationService.RewriteResponse(line[5..].TrimStart(), conversationId);
                data.AppendLine(payload);
                line = "data: " + payload;
            }

            if (line.Length == 0 && data.Length > 0)
            {
                usage.ReadResponse(data.ToString(), false);
            }

            await context.Response.WriteAsync(line + "\n", token);

            if (line.Length == 0)
            {
                await context.Response.Body.FlushAsync(token);
                if (data.Length > 0)
                {
                    using JsonDocument item = ParseEvent(data.ToString());
                    if (item.RootElement.TryGetProperty("type", out JsonElement type)
                        && type.GetString() is "response.completed" or "response.incomplete" or "response.failed" or "error")
                    {
                        return;
                    }

                    data.Clear();
                }
            }
        }

        throw new IOException("The upstream stream ended before a terminal Responses event.");
    }

    private static JsonDocument ParseEvent(string data)
    {
        try
        {
            return JsonDocument.Parse(data);
        }
        catch (JsonException ex)
        {
            throw new IOException("The upstream sent an invalid Responses event.", ex);
        }
    }
}
