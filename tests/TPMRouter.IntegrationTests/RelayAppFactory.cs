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
            ["UsageHistory:DatabasePath"] = Path.Combine(Path.GetTempPath(), "TPMRouter.Tests", $"{Guid.NewGuid():N}.db"),
            ["Relay:SafetyFactor"] = "1.0",
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

    public HttpClient CreateOpenAIClient(string? apiKey = ClientKey)
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
    public const string ResponsesPath = "/openai/v1/responses";

    public static IRequestBuilder ResponseRequest(string deployment) =>
        Request.Create().WithPath(ResponsesPath).UsingPost().WithBody(new JsonPartialMatcher($$"""{ "model": "{{deployment}}" }"""));

    public static bool IsFor(this WireMock.Logging.ILogEntry entry, string deployment) =>
        entry.RequestMessage?.Body?.Contains($"\"model\":\"{deployment}\"") == true;

    public static string ResponseBody(string? text, string status = "completed")
    {
        string output = text is null
            ? ""
            : $$"""{ "type": "message", "id": "msg-1", "status": "completed", "role": "assistant", "content": [{ "type": "output_text", "text": "{{text}}", "annotations": [] }] }""";
        return $$"""
            {
              "id": "resp-1",
              "object": "response",
              "created_at": 1700000000,
              "status": "{{status}}",
              "model": "test-model",
              "output": [{{output}}],
              "usage": { "input_tokens": 12, "output_tokens": 3, "total_tokens": 15 }
            }
            """;
    }

    public static void RespondOk(this WireMockServer server, string deployment, string content = "hello", TimeSpan? delay = null)
    {
        IResponseBuilder response = Response.Create()
            .WithStatusCode(200)
            .WithHeader("Content-Type", "application/json")
            .WithBody(ResponseBody(content));
        if (delay is { } d)
        {
            response = response.WithDelay(d);
        }

        server.Given(ResponseRequest(deployment)).RespondWith(response);
    }

    public static Task<HttpResponseMessage> PostResponseAsync(this HttpClient client, string text, string? instructions = null, CancellationToken cancellationToken = default)
    {
        JsonObject body = new() { ["model"] = "auto", ["input"] = text };
        if (instructions is not null)
        {
            body["instructions"] = instructions;
        }

        return client.PostAsJsonAsync("/v1/responses", body, cancellationToken);
    }
}
