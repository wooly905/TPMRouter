using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using TPMRouter.Api.Options;

namespace TPMRouter.Api.Relay;

public sealed class ConversationService(HttpClient client, IOptions<RelayOptions> options)
{
    private readonly RelayOptions _options = options.Value;

    // Include the resource URI so a reused endpoint ID cannot silently address another resource.
    private static string ResourceKey(EndpointOptions endpoint) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            endpoint.Id + "\n" + endpoint.Endpoint.TrimEnd('/'))))[..24].ToLowerInvariant();

    private static string Encode(EndpointOptions endpoint, string id) => $"conv_router_v1_{ResourceKey(endpoint)}_{id}";

    private (EndpointOptions? Endpoint, string? Id) Resolve(string id)
    {
        foreach (EndpointOptions endpoint in _options.Endpoints)
        {
            string prefix = $"conv_router_v1_{ResourceKey(endpoint)}_";
            if (id.StartsWith(prefix, StringComparison.Ordinal))
            {
                string native = id[prefix.Length..];
                if (native.StartsWith("conv_", StringComparison.Ordinal) && native.Length is > 5 and <= 256
                    && native.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
                {
                    return (endpoint, native);
                }
            }
        }
        return (null, null);
    }

    private static RelayOutcome Missing() => new(404, Error: "The conversation was not found.",
        ErrorParam: "conversation_id", ErrorCode: "not_found");

    public async Task<RelayOutcome> ManageAsync(HttpMethod method, string? id, string suffix,
        string query, JsonObject? body, CancellationToken token)
    {
        EndpointOptions? endpoint;
        string path = "conversations";
        if (id is null)
        {
            endpoint = _options.Endpoints.OrderBy(e => e.Deployments.Min(d => d.Priority))
                .ThenBy(e => e.Id, StringComparer.Ordinal).First();
        }
        else
        {
            string? native;
            (endpoint, native) = Resolve(id);
            if (endpoint is null) return Missing();
            path += "/" + Uri.EscapeDataString(native!) + suffix;
        }

        RelayOutcome result = await SendAsync(endpoint, method, path + query, body, token);
        if (result.StatusCode is >= 200 and < 300 && result.Content is { } content)
        {
            JsonObject json = (JsonObject)JsonNode.Parse(content.ToString())!;
            if (json["object"]?.GetValue<string>() is "conversation" or "conversation.deleted")
            {
                if (json["id"] is not JsonValue value || !value.TryGetValue(out string? native)
                    || string.IsNullOrEmpty(native))
                    return new(502, Error: "The upstream returned an invalid conversation ID.");
                json["id"] = id ?? Encode(endpoint, native);
                result = result with { Content = BinaryData.FromString(json.ToJsonString()) };
            }
        }
        return result;
    }

    public async Task<(ResponsesRequest Request, RelayOutcome? Error)> PrepareAsync(ResponsesRequest request, CancellationToken token)
    {
        JsonNode conversation = request.Body["conversation"]!;
        string id = (conversation is JsonObject obj ? obj["id"]! : conversation).GetValue<string>();
        (EndpointOptions? endpoint, string? native) = Resolve(id);
        if (endpoint is null) return (request, Missing());

        JsonArray history = [];
        HashSet<string> cursors = new(StringComparer.Ordinal);
        string path = $"conversations/{Uri.EscapeDataString(native!)}/items?order=asc&limit=100&include%5B%5D=reasoning.encrypted_content";
        string? after = null;
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(_options.RequestDeadlineSeconds));
        while (true)
        {
            RelayOutcome page = await SendAsync(endpoint, HttpMethod.Get,
                path + (after is null ? "" : "&after=" + Uri.EscapeDataString(after)), null, deadline.Token);
            if (page.StatusCode is < 200 or >= 300)
                return (request, page.StatusCode == RelayOutcome.ClientClosedRequest && !token.IsCancellationRequested
                    ? page with { StatusCode = 504 } : page);
            JsonObject json = (JsonObject)JsonNode.Parse(page.Content!.ToString())!;
            if (json["data"] is not JsonArray items || json["has_more"] is not JsonValue more
                || !more.TryGetValue(out bool hasMore))
            {
                return (request, new(502, Error: "The upstream returned invalid conversation items."));
            }
            foreach (JsonNode? item in items) history.Add(item?.DeepClone());
            if (!hasMore) break;
            after = json["last_id"] is JsonValue cursor && cursor.TryGetValue(out string? next) ? next : null;
            if (string.IsNullOrEmpty(after) || !cursors.Add(after))
            {
                return (request, new(502, Error: "The upstream returned an invalid conversation cursor."));
            }
        }

        request.Body["conversation"] = conversation is JsonObject
            ? new JsonObject { ["id"] = native } : JsonValue.Create(native);
        bool hasImages = history.OfType<JsonObject>()
            .SelectMany(item => item["content"] as JsonArray ?? []).OfType<JsonObject>()
            .Any(part => part["type"]?.GetValue<string>() == "input_image");
        return (request with { EndpointId = endpoint.Id, RouterConversationId = id, History = history,
            HasImages = request.HasImages || hasImages }, null);
    }

    public static string RewriteResponse(string text, string? id)
    {
        if (id is null) return EncryptedReasoning.StripJson(text);
        JsonObject? json;
        try
        {
            json = JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException ex)
        {
            throw new IOException("The upstream sent invalid Responses JSON.", ex);
        }
        JsonObject? response = json?["response"] as JsonObject ?? json;
        if (response?["conversation"] is JsonObject conversation) conversation["id"] = id;
        return json?.ToJsonString() ?? text;
    }

    private async Task<RelayOutcome> SendAsync(EndpointOptions endpoint, HttpMethod method,
        string path, JsonObject? body, CancellationToken token)
    {
        using HttpRequestMessage message = new(method, endpoint.Endpoint.TrimEnd('/') + "/openai/v1/" + path);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
        if (body is not null)
            message.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        try
        {
            using HttpResponseMessage response = await client.SendAsync(message, token);
            string text = await response.Content.ReadAsStringAsync(token);
            if (JsonNode.Parse(text) is not JsonObject)
                return new(502, Error: "The upstream returned invalid conversation JSON.");
            TimeSpan? retry = response.Headers.RetryAfter?.Delta;
            return new((int)response.StatusCode, RetryAfter: retry, Content: BinaryData.FromString(text));
        }
        catch (JsonException)
        {
            return new(502, Error: "The upstream returned invalid conversation JSON.");
        }
        catch (OperationCanceledException)
        {
            return new(token.IsCancellationRequested ? RelayOutcome.ClientClosedRequest : 504,
                Error: "The conversation request was cancelled or timed out.");
        }
        catch (HttpRequestException)
        {
            return new(502, Error: "The upstream connection failed.");
        }
    }
}
