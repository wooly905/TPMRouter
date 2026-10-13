using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace TPMRouter.IntegrationTests;

public sealed class MultiEndpointTests : IDisposable
{
    private readonly WireMockServer _first = WireMockServer.Start();
    private readonly WireMockServer _second = WireMockServer.Start();
    private const string TerminalEvent = "event: response.completed\ndata: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp-1\",\"usage\":{\"input_tokens\":12,\"output_tokens\":3}}}\n\n";
    private const string StreamBody = "event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"output_index\":0,\"delta\":\"hello\"}\n\n" + TerminalEvent;

    public void Dispose()
    {
        _first.Stop();
        _second.Stop();
    }

    [Theory]
    [InlineData(429, false)]
    [InlineData(503, false)]
    [InlineData(429, true)]
    [InlineData(503, true)]
    public async Task ExplicitModel_FailsOverAcrossEndpointsWithCorrectCredentials(int status, bool stream)
    {
        _first.Given(Upstream.ResponseRequest("primary-deployment").WithHeader("Authorization", "Bearer first-key"))
            .RespondWith(Response.Create().WithStatusCode(status)
                .WithHeader("Content-Type", "application/json")
                .WithHeader("retry-after-ms", "30000")
                .WithHeader("x-ratelimit-remaining-tokens", "0")
                .WithBody("""{"error":{"message":"unavailable"}}"""));
        string expected = stream ? StreamBody : Upstream.ResponseBody("from second");
        _second.Given(Upstream.ResponseRequest("backup-deployment").WithHeader("Authorization", "Bearer second-key"))
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", stream ? "text/event-stream" : "application/json")
                .WithHeader("x-ratelimit-remaining-tokens", "5000")
                .WithBody(expected));
        await using RelayAppFactory app = new(
        [
            new("first", _first, [new("primary-deployment", Priority: 1, Model: "shared-model"),
                                  new("unrelated", Priority: 0, Model: "other-model")], "first-key"),
            new("second", _second, [new("backup-deployment", Priority: 2, Model: "shared-model"),
                                    new("unrelated", Priority: 0, Model: "other-model")], "second-key"),
        ]);
        using HttpClient client = app.CreateOpenAIClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "shared-model",
            input = "Hi",
            stream,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, await response.Content.ReadAsStringAsync());
        Assert.Single(_first.LogEntries);
        Assert.Single(_second.LogEntries);
        using HttpClient admin = app.CreateRelayClient(RelayAppFactory.AdminKey);
        JsonElement statuses = await admin.GetFromJsonAsync<JsonElement>("/relay/status");
        JsonElement first = statuses.EnumerateArray().Single(s => s.GetProperty("name").GetString() == "primary-deployment");
        JsonElement second = statuses.EnumerateArray().Single(s => s.GetProperty("name").GetString() == "backup-deployment");
        Assert.Equal("first", first.GetProperty("endpointId").GetString());
        Assert.Equal("second", second.GetProperty("endpointId").GetString());
        Assert.Equal("shared-model", second.GetProperty("model").GetString());
        Assert.Equal(0, first.GetProperty("headerRemainingTokens").GetInt64());
        Assert.Equal(5000, second.GetProperty("headerRemainingTokens").GetInt64());
        Assert.NotEqual(JsonValueKind.Null, first.GetProperty("cooldownUntil").ValueKind);
        Assert.Equal(JsonValueKind.Null, second.GetProperty("cooldownUntil").ValueKind);
    }

    [Theory]
    [InlineData(20, 10, 100000)]
    [InlineData(10, 10, 200000)]
    public async Task Auto_UsesGlobalPriorityAndBudgetWithSameNamedDeployments(int firstPriority, int secondPriority, long secondTpm)
    {
        _first.RespondOk("shared", "from first");
        _second.RespondOk("shared", "from second");
        await using RelayAppFactory app = new(
        [
            new("first", _first, [new("shared", Priority: firstPriority)]),
            new("second", _second, [new("shared", TokensPerMinute: secondTpm, Priority: secondPriority)]),
        ]);
        using HttpClient client = app.CreateOpenAIClient();

        using HttpResponseMessage response = await client.PostResponseAsync("Hi");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("from second", await response.Content.ReadAsStringAsync());
        Assert.Empty(_first.LogEntries);
        Assert.Single(_second.LogEntries);
    }

    [Fact]
    public async Task SameNamedDeployment429_FailsOverWithoutExcludingOtherEndpoint()
    {
        _first.Given(Upstream.ResponseRequest("shared"))
            .RespondWith(Response.Create().WithStatusCode(429)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"error":{"message":"limited"}}"""));
        _second.RespondOk("shared", "from second");
        await using RelayAppFactory app = new(
        [
            new("first", _first, [new("shared", Priority: 1)]),
            new("second", _second, [new("shared", Priority: 2)]),
        ]);
        using HttpClient client = app.CreateOpenAIClient();

        using HttpResponseMessage response = await client.PostResponseAsync("Hi");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(_first.LogEntries);
        Assert.Single(_second.LogEntries);
    }

    [Fact]
    public async Task Timeout_FailsOverToSameNamedDeploymentOnOtherEndpoint()
    {
        _first.RespondOk("shared", delay: TimeSpan.FromSeconds(3));
        _second.RespondOk("shared", "from second");
        await using RelayAppFactory app = new(
        [
            new("first", _first, [new("shared", Priority: 1)]),
            new("second", _second, [new("shared", Priority: 2)]),
        ], s =>
        {
            s["Relay:UpstreamTimeoutSeconds"] = "1";
            s["Relay:RequestDeadlineSeconds"] = "4";
        });
        using HttpClient client = app.CreateOpenAIClient();

        using HttpResponseMessage response = await client.PostResponseAsync("Hi");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("from second", await response.Content.ReadAsStringAsync());
        Assert.Single(_second.LogEntries);
    }

    [Fact]
    public async Task ExplicitModel_DoesNotFailOverToDifferentModel()
    {
        _first.Given(Upstream.ResponseRequest("primary"))
            .RespondWith(Response.Create().WithStatusCode(429)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"error":{"message":"limited"}}"""));
        _second.RespondOk("unrelated");
        await using RelayAppFactory app = new(
        [
            new("first", _first, [new("primary", Model: "requested-model")]),
            new("second", _second, [new("unrelated", Model: "other-model")]),
        ], s => s["Relay:MaxQueueWaitSeconds"] = "0");
        using HttpClient client = app.CreateOpenAIClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "requested-model",
            input = "Hi",
        });

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Single(_first.LogEntries);
        Assert.Empty(_second.LogEntries);
    }

    [Fact]
    public async Task StreamFailureAfterFirstChunk_DoesNotSwitchEndpoints()
    {
        string incomplete = StreamBody[..^TerminalEvent.Length];
        _first.Given(Upstream.ResponseRequest("shared"))
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "text/event-stream").WithBody(incomplete));
        _second.RespondOk("shared");
        await using RelayAppFactory app = new(
        [
            new("first", _first, [new("shared", Priority: 1)]),
            new("second", _second, [new("shared", Priority: 2)]),
        ]);
        using HttpClient client = app.CreateOpenAIClient();

        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            using HttpResponseMessage response = await client.PostAsJsonAsync("/v1/responses", new
            {
                model = "auto",
                input = "Hi",
                stream = true,
            });
            await response.Content.ReadAsStringAsync();
        });

        Assert.Single(_first.LogEntries);
        Assert.Empty(_second.LogEntries);
    }
}
