using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using WireMock.Server;

namespace TPMRouter.IntegrationTests;

[Collection(RedisCollection.Name)]
public sealed class RedisTwoInstanceTests(RedisFixture redis) : IDisposable
{
    private readonly WireMockServer _upstream = WireMockServer.Start();

    public void Dispose() => _upstream.Stop();

    [Fact]
    public async Task TwoInstances_ShareFailedAttemptReservations()
    {
        _upstream.Given(Upstream.ChatRequest("shared"))
            .RespondWith(WireMock.ResponseBuilders.Response.Create().WithStatusCode(400)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"error":{"message":"unsupported"},"usage":{"prompt_tokens":50,"completion_tokens":5}}"""));
        string prefix = $"failed-two-{Guid.NewGuid():N}";
        void Configure(Dictionary<string, string?> settings)
        {
            settings["Relay:BudgetStore"] = "Redis";
            settings["Relay:RedisConnectionString"] = redis.ConnectionString;
            settings["Relay:RedisKeyPrefix"] = prefix;
        }

        await using RelayAppFactory first = new(_upstream, [new("shared")], Configure);
        await using RelayAppFactory second = new(_upstream, [new("shared")], Configure);
        using HttpClient firstClient = first.CreateChatClient();
        using HttpClient secondClient = second.CreateChatClient();
        using HttpResponseMessage response = await firstClient.PostChatAsync("Hi");
        var status = await secondClient.GetFromJsonAsync<System.Text.Json.JsonElement>("/relay/dashboard/status");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(status[0].GetProperty("reservedTokens").GetInt64() > 100);
        Assert.Equal(1, status[0].GetProperty("requestsInWindow").GetInt32());
        Assert.Equal(55, status[0].GetProperty("actualTokens").GetInt64());
    }

    [Fact]
    public async Task TwoInstances_ShareIndependentBudgetsAcrossEndpoints()
    {
        using WireMockServer other = WireMockServer.Start();
        _upstream.RespondOk("shared");
        other.RespondOk("shared");
        string prefix = $"multi-two-{Guid.NewGuid():N}";
        void Configure(Dictionary<string, string?> settings)
        {
            settings["Relay:BudgetStore"] = "Redis";
            settings["Relay:RedisConnectionString"] = redis.ConnectionString;
            settings["Relay:RedisKeyPrefix"] = prefix;
            settings["Relay:MaxQueueWaitSeconds"] = "0";
        }

        TestEndpoint[] endpoints =
        [
            new("first", _upstream, [new("shared", TokensPerMinute: 300)]),
            new("second", other, [new("shared", TokensPerMinute: 300)]),
        ];
        await using RelayAppFactory first = new(endpoints, Configure);
        await using RelayAppFactory second = new(endpoints, Configure);
        using HttpClient firstClient = first.CreateChatClient();
        using HttpClient secondClient = second.CreateChatClient();
        HttpClient[] clients = [firstClient, secondClient];

        HttpResponseMessage[] responses = await Task.WhenAll(
            Enumerable.Range(0, 20).Select(i => clients[i % 2].PostChatAsync("Hi")));

        Assert.Equal(4, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(16, responses.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests));
        Assert.Equal(2, _upstream.LogEntries.Count());
        Assert.Equal(2, other.LogEntries.Count());
        foreach (HttpResponseMessage response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task TwoChatInstances_DoNotExceedTpm()
    {
        _upstream.RespondOk("dep-a");
        string prefix = $"two-{Guid.NewGuid():N}";
        void Configure(Dictionary<string, string?> s)
        {
            s["Relay:BudgetStore"] = "Redis";
            s["Relay:RedisConnectionString"] = redis.ConnectionString;
            s["Relay:RedisKeyPrefix"] = prefix;
            s["Relay:MaxQueueWaitSeconds"] = "1";
        }

        // Each request reserves over 100 tokens including message framing, so only two fit in 300 TPM.
        TestDeployment[] deployments = [new("dep-a", TokensPerMinute: 300, MaxOutputTokens: 100)];
        await using RelayAppFactory first = new(_upstream, deployments, Configure);
        await using RelayAppFactory second = new(_upstream, deployments, Configure);
        HttpClient[] clients = [first.CreateChatClient(), second.CreateChatClient()];

        HttpResponseMessage[] responses = await Task.WhenAll(
            Enumerable.Range(0, 20).Select(i => clients[i % 2].PostChatAsync("Hi")));

        Assert.Equal(2, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(18, responses.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests));
        Assert.Equal(2, _upstream.LogEntries.Count());
    }

    [Fact]
    public async Task TwoChatInstances_ReserveChoicesAtomically()
    {
        _upstream.RespondOk("dep-a");
        string prefix = $"chat-two-{Guid.NewGuid():N}";
        void Configure(Dictionary<string, string?> s)
        {
            s["Relay:BudgetStore"] = "Redis";
            s["Relay:RedisConnectionString"] = redis.ConnectionString;
            s["Relay:RedisKeyPrefix"] = prefix;
            s["Relay:MaxQueueWaitSeconds"] = "0";
        }

        TestDeployment[] deployments = [new("dep-a", TokensPerMinute: 300, MaxOutputTokens: 100)];
        await using RelayAppFactory first = new(_upstream, deployments, Configure);
        await using RelayAppFactory second = new(_upstream, deployments, Configure);
        using HttpClient firstClient = first.CreateClient();
        using HttpClient secondClient = second.CreateClient();
        HttpClient[] clients = [firstClient, secondClient];
        foreach (HttpClient client in clients)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", RelayAppFactory.ClientKey);
        }

        const string body = """{"model":"auto","messages":[{"role":"user","content":"Hi"}],"n":2,"max_completion_tokens":100}""";
        HttpResponseMessage[] responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            clients[i % 2].PostAsync("/v1/chat/completions", new StringContent(body, Encoding.UTF8, "application/json"))));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Equal(19, responses.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests));
        Assert.Single(_upstream.LogEntries);
        foreach (HttpResponseMessage response in responses)
        {
            response.Dispose();
        }
    }
}
