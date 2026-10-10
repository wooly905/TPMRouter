using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TPMRouter.Api.Relay;

namespace TPMRouter.Api.Endpoints;

public static class ResponsesEndpoint
{
    public static async Task<IResult> HandleAsync(HttpContext context, RelayService service)
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

        RelayOutcome? invalid = ResponsesRequest.Validate(body, out ResponsesRequest? request);
        if (invalid is not null)
        {
            return OpenAIError.Result(invalid);
        }

        RelayOutcome outcome = await service.RelayResponseAsync(request!,
                                                                (response, token) => CopyStreamAsync(response, context, token),
                                                                () => context.Response.HasStarted,
                                                                context.RequestAborted);
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
            ? Results.Content(outcome.Content.ToString(), "application/json", Encoding.UTF8, outcome.StatusCode)
            : OpenAIError.Result(outcome);
    }

    private static async Task CopyStreamAsync(PipelineResponse response, HttpContext context, CancellationToken token)
    {
        using StreamReader reader = new(response.ContentStream!, Encoding.UTF8);
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        StringBuilder data = new();
        while (await reader.ReadLineAsync(token) is { } line)
        {
            await context.Response.WriteAsync(line + "\n", token);
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                data.AppendLine(line[5..].TrimStart());
            }

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
