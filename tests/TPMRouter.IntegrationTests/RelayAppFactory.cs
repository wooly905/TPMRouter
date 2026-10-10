using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace TPMRouter.IntegrationTests;

public sealed record TestDeployment(string Name, long TokensPerMinute = 100_000, int RequestsPerMinute = 0, int MaxOutputTokens = 100, int Priority = 1, string? Model = null);

public sealed record TestEndpoint(string Id, WireMockServer Upstream, IReadOnlyList<TestDeployment> Deployments, string ApiKey = "upstream-key");

public sealed class RelayAppFactory(IReadOnlyList<TestEndpoint> endpoints, Action<Dictionary<string, string?>>? configure = null, Action<IServiceCollection>? services = null)
    : WebApplicationFactory<Program>
{
    public const string ClientKey = "test-client-key";
    public const string AdminKey = "test-admin-key";

    public RelayAppFactory(WireMockServer upstream, IReadOnlyList<TestDeployment> deployments,
                           Action<Dictionary<string, string?>>? configure = null, Action<IServiceCollection>? services = null)
        : this([new("test", upstream, deployments)], configure, services)
    {
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Dictionary<string, string?> settings = new()
        {
            ["Relay:SafetyFactor"] = "1.0",
            ["Relay:MaxInputCharacters"] = "100000",
            ["Relay:MaxQueueWaitSeconds"] = "5",
            ["Relay:ErrorCooldownSeconds"] = "10",
            ["Relay:UpstreamTimeoutSeconds"] = "10",
            ["Relay:RequestDeadlineSeconds"] = "20",
            ["Relay:ClientApiKey"] = ClientKey,
            ["Relay:AdminApiKey"] = AdminKey,
        };

        for (int i = 0; i < endpoints.Count; i++)
        {
            TestEndpoint endpoint = endpoints[i];
            string prefix = $"Relay:Endpoints:{i}";
            settings[$"{prefix}:Id"] = endpoint.Id;
            settings[$"{prefix}:Endpoint"] = endpoint.Upstream.Url!;
            settings[$"{prefix}:ApiKey"] = endpoint.ApiKey;
            for (int j = 0; j < endpoint.Deployments.Count; j++)
            {
                TestDeployment d = endpoint.Deployments[j];
                string deploymentPrefix = $"{prefix}:Deployments:{j}";
                settings[$"{deploymentPrefix}:Name"] = d.Name;
                settings[$"{deploymentPrefix}:Model"] = d.Model ?? d.Name;
                settings[$"{deploymentPrefix}:TokensPerMinute"] = d.TokensPerMinute.ToString();
                settings[$"{deploymentPrefix}:RequestsPerMinute"] = d.RequestsPerMinute.ToString();
                settings[$"{deploymentPrefix}:MaxOutputTokens"] = d.MaxOutputTokens.ToString();
                settings[$"{deploymentPrefix}:Priority"] = d.Priority.ToString();
            }
        }

        configure?.Invoke(settings);

        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.Sources.Clear();
            config.AddInMemoryCollection(settings);
        });

        if (services is not null)
        {
            builder.ConfigureServices(services);
        }
    }

    public HttpClient CreateRelayClient(string? apiKey = ClientKey)
    {
        HttpClient client = CreateClient();
        if (apiKey is not null)
        {
            client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        }

        return client;
    }

    public HttpClient CreateChatClient(string? apiKey = ClientKey)
    {
        HttpClient client = CreateClient();
        if (apiKey is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        return client;
    }
}

internal static class Upstream
{
    public const string ChatPath = "/openai/v1/chat/completions";

    public static IRequestBuilder ChatRequest(string deployment) =>
        Request.Create().WithPath(ChatPath).UsingPost().WithBody(new JsonPartialMatcher($$"""{ "model": "{{deployment}}" }"""));

    public static bool IsFor(this WireMock.Logging.ILogEntry entry, string deployment) =>
        entry.RequestMessage?.Body?.Contains($"\"model\":\"{deployment}\"") == true;

    public static string Completion(string? content, string finishReason = "stop") =>
        $$"""
        {
          "id": "chatcmpl-1",
          "object": "chat.completion",
          "created": 1700000000,
          "model": "test-model",
          "choices": [
            { "index": 0, "message": { "role": "assistant", "content": {{(content is null ? "null" : $"\"{content}\"")}} }, "finish_reason": "{{finishReason}}" }
          ],
          "usage": { "prompt_tokens": 12, "completion_tokens": 3, "total_tokens": 15 }
        }
        """;

    public static void RespondOk(this WireMockServer server, string deployment, string content = "hello", TimeSpan? delay = null)
    {
        IResponseBuilder response = Response.Create()
            .WithStatusCode(200)
            .WithHeader("Content-Type", "application/json")
            .WithBody(Completion(content));
        if (delay is { } d)
        {
            response = response.WithDelay(d);
        }

        server.Given(ChatRequest(deployment)).RespondWith(response);
    }

    public static Task<HttpResponseMessage> PostChatAsync(this HttpClient client, string text, string? systemPrompt = null, CancellationToken cancellationToken = default)
    {
        JsonArray messages = [];
        if (systemPrompt is not null)
        {
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = systemPrompt });
        }

        messages.Add(new JsonObject { ["role"] = "user", ["content"] = text });
        return client.PostAsJsonAsync("/v1/chat/completions", new JsonObject { ["model"] = "auto", ["messages"] = messages }, cancellationToken);
    }
}
