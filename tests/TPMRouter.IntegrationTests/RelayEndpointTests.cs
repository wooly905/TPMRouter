using System.Diagnostics;
using System.ClientModel.Primitives;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using TPMRouter.Api.Upstream;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace TPMRouter.IntegrationTests;

public sealed class RelayEndpointTests : IDisposable
{
    private readonly WireMockServer _upstream = WireMockServer.Start();

    public void Dispose() => _upstream.Stop();

    [Fact]
    public async Task Post_ReturnsOutputFromDeployment()
    {
        _upstream.RespondOk("dep-a", "hello");
        await using RelayAppFactory app = new(_upstream, [new("dep-a")]);

        HttpResponseMessage response = await app.CreateOpenAIClient().PostResponseAsync("Hi", "You are helpful.");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("hello", body.GetProperty("output")[0].GetProperty("content")[0].GetProperty("text").GetString());
        Assert.True(_upstream.LogEntries.Single().IsFor("dep-a"));
        Assert.Equal("completed", body.GetProperty("status").GetString());
        Assert.Equal(12, body.GetProperty("usage").GetProperty("input_tokens").GetInt32());
        Assert.Equal(3, body.GetProperty("usage").GetProperty("output_tokens").GetInt32());
        Assert.Equal(15, body.GetProperty("usage").GetProperty("total_tokens").GetInt32());

        JsonObject upstreamBody = (JsonObject)JsonNode.Parse(_upstream.LogEntries.Single().RequestMessage!.Body!)!;
        Assert.Equal("You are helpful.", upstreamBody["instructions"]!.GetValue<string>());
        Assert.Equal(100, upstreamBody["max_output_tokens"]!.GetValue<int>());
        Assert.Null(upstreamBody["max_tokens"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?api-version=1.0")]
    public async Task Post_RemovedRelay_Returns404(string query)
    {
        await using RelayAppFactory app = new(_upstream, [new("dep-a")]);

        using HttpResponseMessage response = await app.CreateOpenAIClient()
            .PostAsJsonAsync($"/relay{query}", new { systemPrompt = "Be helpful.", text = "Hi" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(_upstream.LogEntries);
    }

    [Fact]
    public async Task Post_EmptyText_Returns200()
    {
        await using RelayAppFactory app = new(_upstream, [new("dep-a")]);

        _upstream.RespondOk("dep-a");
        HttpResponseMessage response = await app.CreateOpenAIClient().PostResponseAsync("");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-key")]
    [InlineData(RelayAppFactory.AdminKey)]
    public async Task Post_WithoutCorrectKey_Returns401(string? apiKey)
    {
        await using RelayAppFactory app = new(_upstream, [new("dep-a")]);

        HttpResponseMessage response = await app.CreateOpenAIClient(apiKey).PostResponseAsync("Hi");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_upstream.LogEntries);
    }

    [Fact]
    public async Task Upstream429_SendsRequestToOtherDeployment()
    {
        _upstream.Given(Upstream.ResponseRequest("dep-a"))
            .RespondWith(Response.Create()
                .WithStatusCode(429)
                .WithHeader("retry-after-ms", "30000")
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{ "error": { "code": "429", "message": "Rate limit." } }"""));
        _upstream.RespondOk("dep-b", "from b");
        await using RelayAppFactory app = new(_upstream, [new("dep-a", Priority: 1), new("dep-b", Priority: 2)]);

        HttpResponseMessage response = await app.CreateOpenAIClient().PostResponseAsync("Hi");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("from b", body.GetProperty("output")[0].GetProperty("content")[0].GetProperty("text").GetString());
        // The SDK must not retry the 429 on the same deployment.
        Assert.Single(_upstream.LogEntries, e => e.IsFor("dep-a"));
    }

    [Fact]
    public async Task Upstream400_ReturnsErrorWithoutRetry()
    {
        _upstream.Given(Upstream.ResponseRequest("dep-a"))
            .RespondWith(Response.Create()
                .WithStatusCode(400)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{ "error": { "code": "bad", "message": "Bad input." } }"""));
        _upstream.RespondOk("dep-b");
        await using RelayAppFactory app = new(_upstream, [new("dep-a", Priority: 1), new("dep-b", Priority: 2)]);

        HttpResponseMessage response = await app.CreateOpenAIClient().PostResponseAsync("Hi");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Bad input.", body.GetProperty("error").GetProperty("message").GetString());
        Assert.Single(_upstream.LogEntries);
    }

    [Fact]
    public async Task AllDeploymentsFull_Returns429WithRetryAfter()
    {
        _upstream.RespondOk("dep-a");
        await using RelayAppFactory app = new(
            _upstream,
            [new("dep-a", TokensPerMinute: 1000, MaxOutputTokens: 600)],
            s => s["Relay:MaxQueueWaitSeconds"] = "1");
        HttpClient client = app.CreateOpenAIClient();

        Assert.Equal(HttpStatusCode.OK, (await client.PostResponseAsync("Hi")).StatusCode);
        Stopwatch stopwatch = Stopwatch.StartNew();
        HttpResponseMessage response = await client.PostResponseAsync("Hi");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter?.Delta);
        Assert.InRange(response.Headers.RetryAfter!.Delta!.Value.TotalSeconds, 1, 60);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Took {stopwatch.Elapsed}.");
        Assert.Single(_upstream.LogEntries);
    }

    [Fact]
    public async Task QueueWait_SendsRequestAfterCooldownEnds()
    {
        _upstream.Given(Upstream.ResponseRequest("dep-a"))
            .InScenario("cooldown")
            .WillSetStateTo("recovered")
            .RespondWith(Response.Create().WithStatusCode(429).WithHeader("retry-after-ms", "500"));
        _upstream.Given(Upstream.ResponseRequest("dep-a"))
            .InScenario("cooldown")
            .WhenStateIs("recovered")
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(Upstream.ResponseBody("late")));
        await using RelayAppFactory app = new(_upstream, [new("dep-a")]);
        HttpClient client = app.CreateOpenAIClient();

        HttpResponseMessage first = await client.PostResponseAsync("Hi");
        Stopwatch stopwatch = Stopwatch.StartNew();
        HttpResponseMessage second = await client.PostResponseAsync("Hi");

        Assert.Equal(HttpStatusCode.TooManyRequests, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(300), $"Took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task ClientCancel_StopsUpstreamRequest()
    {
        _upstream.RespondOk("dep-a", delay: TimeSpan.FromSeconds(10));
        RecordingUpstreamClient? recorder = null;
        await using RelayAppFactory app = new(_upstream, [new("dep-a")], services: s =>
        {
            s.AddSingleton(sp => recorder = new RecordingUpstreamClient(ActivatorUtilities.CreateInstance<AzureOpenAIUpstreamClient>(sp)));
            s.AddSingleton<IUpstreamClient>(sp => sp.GetRequiredService<RecordingUpstreamClient>());
        });
        HttpClient client = app.CreateOpenAIClient();
        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.PostResponseAsync("Hi", cancellationToken: cts.Token));

        Task<Exception?> completed = recorder!.Completed.Task;
        Assert.Same(completed, await Task.WhenAny(completed, Task.Delay(TimeSpan.FromSeconds(5))));
        Assert.IsAssignableFrom<OperationCanceledException>(await completed);
    }

    [Fact]
    public async Task SlowUpstreamOnAllDeployments_Returns504AfterDeadline()
    {
        _upstream.RespondOk("dep-a", delay: TimeSpan.FromSeconds(10));
        _upstream.RespondOk("dep-b", delay: TimeSpan.FromSeconds(10));
        await using RelayAppFactory app = new(
            _upstream,
            [new("dep-a"), new("dep-b")],
            s =>
            {
                s["Relay:UpstreamTimeoutSeconds"] = "1";
                s["Relay:RequestDeadlineSeconds"] = "2";
            });

        HttpClient client = app.CreateOpenAIClient();
        Stopwatch stopwatch = Stopwatch.StartNew();
        HttpResponseMessage response = await client.PostResponseAsync("Hi");

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.InRange(stopwatch.Elapsed.TotalSeconds, 1.5, 6);

        // WireMock logs a delayed request only after it responds, so check the attempts in the tracker.
        JsonElement status = await app.CreateRelayClient(RelayAppFactory.AdminKey).GetFromJsonAsync<JsonElement>("/relay/status");
        Assert.All(status.EnumerateArray(), s => Assert.Equal(1, s.GetProperty("requestsInWindow").GetInt32()));
    }

    [Fact]
    public async Task IncompleteResponse_Returns200WithEmptyOutput()
    {
        _upstream.Given(Upstream.ResponseRequest("dep-a"))
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(Upstream.ResponseBody(null, "incomplete")));
        await using RelayAppFactory app = new(_upstream, [new("dep-a")]);

        HttpResponseMessage response = await app.CreateOpenAIClient().PostResponseAsync("Hi");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("output").GetArrayLength());
        Assert.Equal("incomplete", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Healthz_Returns200WithoutKey()
    {
        await using RelayAppFactory app = new(_upstream, [new("dep-a")]);

        HttpResponseMessage response = await app.CreateRelayClient(apiKey: null).GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Version_ReturnsAssemblyVersionWithoutKey()
    {
        await using RelayAppFactory app = new(_upstream, [new("dep-a")]);

        JsonElement body = await app.CreateRelayClient(apiKey: null).GetFromJsonAsync<JsonElement>("/relay/version");

        Version expected = typeof(Program).Assembly.GetName().Version!;
        Assert.StartsWith(expected.ToString(3), body.GetProperty("version").GetString());
    }

    [Fact]
    public async Task DashboardStatus_ReturnsBudgetsWithoutApiKey()
    {
        await using RelayAppFactory app = new(_upstream, [new("dep-a", TokensPerMinute: 10_000, RequestsPerMinute: 50, Priority: 9)]);
        using HttpClient client = app.CreateRelayClient(apiKey: null);

        using HttpResponseMessage response = await client.GetAsync("/relay/dashboard/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement status = (await response.Content.ReadFromJsonAsync<JsonElement>())[0];
        Assert.Equal("test", status.GetProperty("endpointId").GetString());
        Assert.Equal("dep-a", status.GetProperty("model").GetString());
        Assert.Equal(9, status.GetProperty("priority").GetInt32());
        Assert.Equal(10_000, status.GetProperty("availableTokens").GetInt64());
        Assert.Equal(50, status.GetProperty("availableRequests").GetInt64());
        Assert.False(status.TryGetProperty("apiKey", out _));
    }

    [Fact]
    public async Task Status_RequiresAdminKeyAndShowsTrackerValues()
    {
        _upstream.Given(Upstream.ResponseRequest("dep-a"))
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithHeader("x-ratelimit-remaining-tokens", "5000")
                .WithHeader("x-ratelimit-remaining-requests", "40")
                .WithBody(Upstream.ResponseBody("ok")));
        await using RelayAppFactory app = new(_upstream, [new("dep-a", TokensPerMinute: 10_000, RequestsPerMinute: 50, MaxOutputTokens: 100)]);
        await app.CreateOpenAIClient().PostResponseAsync("Hi");

        HttpResponseMessage denied = await app.CreateRelayClient(RelayAppFactory.ClientKey).GetAsync("/relay/status");
        HttpResponseMessage allowed = await app.CreateRelayClient(RelayAppFactory.AdminKey).GetAsync("/relay/status");

        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        JsonElement status = (await allowed.Content.ReadFromJsonAsync<JsonElement>())[0];
        Assert.Equal("dep-a", status.GetProperty("name").GetString());
        Assert.Equal(10_000, status.GetProperty("tokensPerMinute").GetInt64());
        Assert.Equal(1, status.GetProperty("requestsInWindow").GetInt32());
        Assert.Equal(5000, status.GetProperty("headerRemainingTokens").GetInt64());
        Assert.Equal(5000, status.GetProperty("availableTokens").GetInt64());
        Assert.Equal(40, status.GetProperty("availableRequests").GetInt64());
    }

    private sealed class RecordingUpstreamClient(IUpstreamClient inner) : IUpstreamClient
    {
        public TaskCompletionSource<Exception?> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<UpstreamResult> CreateResponseAsync(string deployment, JsonObject request, Func<PipelineResponse, CancellationToken, Task> onStream, CancellationToken cancellationToken)
        {
            try
            {
                UpstreamResult result = await inner.CreateResponseAsync(deployment, request, onStream, cancellationToken);
                Completed.TrySetResult(null);
                return result;
            }
            catch (Exception ex)
            {
                Completed.TrySetResult(ex);
                throw;
            }
        }
    }
}
