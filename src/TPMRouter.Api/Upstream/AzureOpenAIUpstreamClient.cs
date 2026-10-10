using System.ClientModel;
using System.ClientModel.Primitives;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;
using TPMRouter.Api.Options;

namespace TPMRouter.Api.Upstream;

public interface IUpstreamClient
{
    Task<UpstreamResult> CompleteChatAsync(string deployment, JsonObject request, Func<PipelineResponse, CancellationToken, Task> onStream, CancellationToken cancellationToken);
}

public readonly record struct RateLimitHeaders(long? RemainingTokens, long? RemainingRequests, TimeSpan? RetryAfter);

/// <param name="Status">HTTP status code from Azure OpenAI, or 0 when no response was received.</param>
public sealed record UpstreamResult(int Status,
                                    string? ErrorMessage,
                                    RateLimitHeaders RateLimits,
                                    BinaryData? Content = null,
                                    bool Streamed = false);

public sealed class AzureOpenAIUpstreamClient : IUpstreamClient
{
    private readonly Dictionary<string, ChatClient> _chatClients;

    public AzureOpenAIUpstreamClient(IOptions<RelayOptions> options)
    {
        _chatClients = new(StringComparer.Ordinal);
        foreach (EndpointOptions endpoint in options.Value.Endpoints)
        {
            OpenAIClient client = new(new ApiKeyCredential(endpoint.ApiKey), new OpenAIClientOptions
            {
                // Azure OpenAI v1 API: the deployment name is sent as the model.
                Endpoint = new Uri(endpoint.Endpoint.TrimEnd('/') + "/openai/v1/"),
                RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
                NetworkTimeout = TimeSpan.FromSeconds(options.Value.UpstreamTimeoutSeconds),
            });
            foreach (DeploymentOptions deployment in endpoint.Deployments)
            {
                DeploymentTarget target = new(endpoint.Id, deployment);
                _chatClients.Add(target.Key, client.GetChatClient(deployment.Name));
            }
        }
    }

    public async Task<UpstreamResult> CompleteChatAsync(string deployment,
                                                        JsonObject request,
                                                        Func<PipelineResponse, CancellationToken, Task> onStream,
                                                        CancellationToken cancellationToken)
    {
        bool stream = request["stream"]?.GetValue<bool>() == true;
        using BinaryContent content = BinaryContent.Create(BinaryData.FromString(request.ToJsonString()));
        try
        {
            ClientResult result = await _chatClients[deployment].CompleteChatAsync(content, new RequestOptions
            {
                CancellationToken = cancellationToken,
                BufferResponse = !stream,
                ErrorOptions = ClientErrorBehaviors.NoThrow,
            });
            using PipelineResponse raw = result.GetRawResponse();
            RateLimitHeaders headers = ReadRateLimits(raw.Headers);
            if (raw.Status is >= 200 and < 300 && stream)
            {
                await onStream(raw, cancellationToken);
                return new UpstreamResult(raw.Status, null, headers, Streamed: true);
            }

            BinaryData body = stream
                ? await BinaryData.FromStreamAsync(raw.ContentStream!, cancellationToken)
                : raw.Content;
            if (raw.Status is >= 200 and < 300)
            {
                return new UpstreamResult(raw.Status, null, headers, Content: body);
            }

            string? message = ReadErrorMessage(body);
            return new UpstreamResult(raw.Status, message ?? "The upstream request failed.", headers,
                                      Content: message is not null ? body : null);
        }
        catch (ClientResultException ex)
        {
            using PipelineResponse? raw = ex.GetRawResponse();
            return new UpstreamResult(ex.Status, "The upstream request failed.",
                                      raw is null ? default : ReadRateLimits(raw.Headers));
        }
    }

    public static RateLimitHeaders ReadRateLimits(PipelineResponseHeaders headers)
    {
        long? remainingTokens = TryGetLong(headers, "x-ratelimit-remaining-tokens");
        long? remainingRequests = TryGetLong(headers, "x-ratelimit-remaining-requests");

        TimeSpan? retryAfter = null;
        if (headers.TryGetValue("retry-after-ms", out string? ms)
            && double.TryParse(ms, NumberStyles.Float, CultureInfo.InvariantCulture, out double milliseconds)
            && milliseconds >= 0)
        {
            retryAfter = TimeSpan.FromMilliseconds(milliseconds);
        }
        else if (headers.TryGetValue("retry-after", out string? seconds)
            && double.TryParse(seconds, NumberStyles.Float, CultureInfo.InvariantCulture, out double secondsValue)
            && secondsValue >= 0)
        {
            retryAfter = TimeSpan.FromSeconds(secondsValue);
        }

        return new RateLimitHeaders(remainingTokens, remainingRequests, retryAfter);
    }

    private static long? TryGetLong(PipelineResponseHeaders headers, string name) =>
        headers.TryGetValue(name, out string? value) && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long result)
            ? result
            : null;

    private static string? ReadErrorMessage(BinaryData content)
    {
        if (content.ToMemory().IsEmpty)
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(content.ToMemory());
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out JsonElement error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out JsonElement message)
                && message.ValueKind == JsonValueKind.String)
            {
                return message.GetString();
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }
}
