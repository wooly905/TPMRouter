using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace TPMRouter.IntegrationTests;

[Collection(RedisCollection.Name)]
public sealed class RedisConversationTests(RedisFixture redis)
{
    [Fact]
    public async Task TwoInstances_ShareConversationRouteHistoryAndRpmBudget()
    {
        using WireMockServer upstream = WireMockServer.Start();
        using WireMockServer backup = WireMockServer.Start();
        upstream.Given(Request.Create().WithPath("/openai/v1/conversations").UsingPost())
            .RespondWith(Response.Create().WithHeader("Content-Type", "application/json")
                .WithBody("""{"id":"conv_shared","object":"conversation","created_at":1700000000,"metadata":{}}"""));
        upstream.Given(Request.Create().WithPath("/openai/v1/conversations/conv_shared/items").UsingGet())
            .RespondWith(Response.Create().WithHeader("Content-Type", "application/json")
                .WithBody("""{"data":[{"id":"msg_one","type":"message","role":"user","content":"earlier turn"}],"has_more":false}"""));
        upstream.RespondOk("home");
        string prefix = $"conversation-{Guid.NewGuid():N}";
        void Configure(Dictionary<string, string?> settings)
        {
            settings["Relay:BudgetStore"] = "Redis";
            settings["Relay:RedisConnectionString"] = redis.ConnectionString;
            settings["Relay:RedisKeyPrefix"] = prefix;
            settings["Relay:MaxQueueWaitSeconds"] = "0";
        }
        TestEndpoint[] endpoints = [new("home", upstream, [new("home", RequestsPerMinute: 1)]),
            new("backup", backup, [new("backup", Priority: 2)])];
        await using RelayAppFactory first = new(endpoints, Configure);
        await using RelayAppFactory second = new(endpoints, Configure);
        using HttpClient a = first.CreateOpenAIClient();
        using HttpClient b = second.CreateOpenAIClient();
        using HttpResponseMessage create = await a.PostAsJsonAsync("/v1/conversations", new { });
        string id = (await create.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<string>();
        using HttpResponseMessage turn = await b.PostAsJsonAsync("/v1/responses", new { model = "auto", conversation = id, input = "new turn" });
        Assert.Equal(HttpStatusCode.OK, turn.StatusCode);
        using HttpResponseMessage limited = await a.PostAsJsonAsync("/v1/responses", new { model = "auto", conversation = id, input = "next turn" });
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);
        JsonArray status = (await a.GetFromJsonAsync<JsonArray>("/relay/dashboard/status"))!;
        Assert.Equal(1, status[0]!["requestsInWindow"]!.GetValue<int>());
        Assert.True(status[0]!["reservedTokens"]!.GetValue<long>() > 100);
        Assert.Empty(backup.LogEntries);
        Assert.Single(upstream.LogEntries, e => e.RequestMessage!.Path == Upstream.ResponsesPath);
    }
}
