using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TPMRouter.Api.Relay;
using TPMRouter.Api.Usage;

namespace TPMRouter.Api.Endpoints;

public static class ChatCompletionsEndpoint
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

        RelayOutcome? invalid = ChatCompletionsRequest.Validate(body, out ChatCompletionsRequest? request);
        if (invalid is not null)
        {
            return OpenAIError.Result(invalid);
        }

        UsageRecordBuilder streamedUsage = new("");
        RelayOutcome outcome = await service.RelayChatAsync(request!,
                                                            (response, token) => CopyStreamAsync(response, context, streamedUsage, token),
                                                            () => context.Response.HasStarted,
                                                            context.RequestAborted,
                                                            streamedUsage);
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

    private static async Task CopyStreamAsync(PipelineResponse response, HttpContext context,
                                               UsageRecordBuilder usage, CancellationToken token)
    {
        using StreamReader reader = new(response.ContentStream!, Encoding.UTF8);
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        bool done = false;
        StringBuilder data = new();
        while (await reader.ReadLineAsync(token) is { } line)
        {
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (line[5..].Trim() == "[DONE]")
                {
                    done = true;
                }
                else
                {
                    data.AppendLine(line[5..].TrimStart());
                }
            }

            if (line.Length == 0 && data.Length > 0)
            {
                usage.ReadResponse(data.ToString(), false);
                data.Clear();
            }

            await context.Response.WriteAsync(line + "\n", token);

            if (line.Length == 0)
            {
                await context.Response.Body.FlushAsync(token);
                if (done)
                {
                    return;
                }
            }
        }

        if (!done)
        {
            throw new IOException("The upstream stream ended before [DONE].");
        }

        await context.Response.Body.FlushAsync(token);
    }
}
