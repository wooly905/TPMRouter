using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json;
using TPMRouter.Api.Relay;
using TPMRouter.Api.Usage;

namespace TPMRouter.Api.Endpoints;

public static class ChatCompletionEndpoint
{
    public static Task<IResult> HandleAsync(HttpContext context, RelayService service) =>
        ResponsesEndpoint.HandleRequestAsync(context, service, true);

    internal static async Task CopyStreamAsync(PipelineResponse response,
                                               HttpContext context,
                                               UsageRecordBuilder usage,
                                               CancellationToken token)
    {
        using StreamReader reader = new(response.ContentStream!, Encoding.UTF8);
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        StringBuilder data = new();

        while (await reader.ReadLineAsync(token) is { } line)
        {
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                data.AppendLine(line[5..].TrimStart());
            }

            if (line.Length == 0 && data.Length > 0)
            {
                string payload = data.ToString().Trim();

                if (payload != "[DONE]")
                {
                    try
                    {
                        using JsonDocument item = JsonDocument.Parse(payload);

                        if (item.RootElement.ValueKind != JsonValueKind.Object
                            || item.RootElement.TryGetProperty("error", out _))
                        {
                            throw new IOException("The upstream sent an invalid Chat Completions event.");
                        }
                    }
                    catch (JsonException ex)
                    {
                        throw new IOException("The upstream sent an invalid Chat Completions event.", ex);
                    }

                    usage.ReadResponse(payload, false);
                }

                await context.Response.WriteAsync("data: " + payload + "\n\n", token);
                await context.Response.Body.FlushAsync(token);
                data.Clear();

                if (payload == "[DONE]")
                {
                    return;
                }
            }
        }

        throw new IOException("The upstream stream ended before the [DONE] event.");
    }
}
