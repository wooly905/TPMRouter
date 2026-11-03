using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using OpenAI;
using OpenAI.Conversations;
using OpenAI.Responses;
using TPMRouter.Api.Budget;
using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

#pragma warning disable OPENAI001

namespace TPMRouter.IntegrationTests;

public sealed class ConversationEndpointTests : IDisposable
{
    private const string Path = "/openai/v1/conversations";
    private const string Conversation = """{"id":"conv_native","object":"conversation","created_at":1700000000,"metadata":{}}""";
    private readonly WireMockServer _upstream = WireMockServer.Start();

    public void Dispose() => _upstream.Stop();

    private void Stub(string method, string path, string body, int status = 200) =>
        _upstream.Given(Request.Create().WithPath(path).UsingMethod(method))
            .RespondWith(Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json").WithBody(body));

    private async Task<string> CreateAsync(HttpClient client)
    {
        Stub("POST", Path, Conversation);
        using HttpResponseMessage response = await client.PostAsJsonAsync("/v1/conversations", new { metadata = new { topic = "test" } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonObject body = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        string id = body["id"]!.GetValue<string>();
        Assert.StartsWith("conv_router_", id);
        return id;
    }

    private void History(string items = "[]") => Stub("GET", Path + "/conv_native/items",
        $$"""{"object":"list","data":{{items}},"first_id":null,"last_id":null,"has_more":false}""");

    private static Task<HttpResponseMessage> Turn(HttpClient client, string id, JsonNode input, bool objectForm = false) =>
        client.PostAsJsonAsync("/v1/responses", new JsonObject
        {
            ["model"] = "auto", ["input"] = input,
            ["conversation"] = objectForm ? new JsonObject { ["id"] = id } : JsonValue.Create(id),
        });

    [Fact]
    public async Task OfficialSdk_CreatesConversationAndContinuesResponse()
    {
        Stub("POST", Path, Conversation);
        History();
        JsonObject responseBody = (JsonObject)JsonNode.Parse(Upstream.ResponseBody("hello"))!;
        responseBody["conversation"] = new JsonObject { ["id"] = "conv_native" };
        Stub("POST", Upstream.ResponsesPath, responseBody.ToJsonString());
        await using RelayAppFactory app = new(_upstream, [new("dep")]);
        using HttpClient transport = app.CreateClient();
        OpenAIClient sdk = new(new ApiKeyCredential(RelayAppFactory.ClientKey), new OpenAIClientOptions
        {
            Endpoint = new Uri("http://localhost/v1/"), Transport = new HttpClientPipelineTransport(transport),
            RetryPolicy = new ClientRetryPolicy(0),
        });
        ConversationResource conversation = await sdk.GetConversationClient().CreateConversationAsync(new ConversationCreationOptions());
        CreateResponseOptions options = new()
        {
            Model = "auto", ConversationOptions = new ResponseConversationOptions(conversation.Id),
        };
        options.InputItems.Add(ResponseItem.CreateUserMessageItem("Hello"));
        ResponseResult response = await sdk.GetResponsesClient().CreateResponseAsync(options);
        Assert.Equal("hello", response.GetOutputText());
        options.InputItems.Clear();
        options.InputItems.Add(ResponseItem.CreateUserMessageItem("Continue"));
        response = await sdk.GetResponsesClient().CreateResponseAsync(options);
        Assert.Equal("hello", response.GetOutputText());
        Assert.StartsWith("conv_router_", conversation.Id);
    }

    [Fact]
    public async Task Lifecycle_ForwardsMetadataItemsPaginationAndDeletion()
    {
        await using RelayAppFactory app = new(_upstream, [new("dep")]);
        using HttpClient client = app.CreateOpenAIClient();
        string id = await CreateAsync(client);
        string route = "/v1/conversations/" + id;
        Stub("GET", Path + "/conv_native", Conversation);
        Stub("POST", Path + "/conv_native", Conversation.Replace("{}", "{\"topic\":\"updated\"}"));
        Stub("POST", Path + "/conv_native/items", """{"object":"list","data":[{"id":"msg_one","type":"message","role":"user","content":[{"type":"input_text","text":"hello"}]}],"first_id":"msg_one","last_id":"msg_one","has_more":false}""");
        Stub("GET", Path + "/conv_native/items/msg_one", """{"id":"msg_one","type":"message","role":"user","content":[]}""");
        Stub("DELETE", Path + "/conv_native/items/msg_one", Conversation);
        Stub("DELETE", Path + "/conv_native", """{"id":"conv_native","object":"conversation.deleted","deleted":true}""");
        History();

        JsonObject? retrieved = await client.GetFromJsonAsync<JsonObject>(route);
        Assert.Equal(id, retrieved!["id"]!.GetValue<string>());
        using HttpResponseMessage update = await client.PostAsJsonAsync(route, new { metadata = new { topic = "updated" } });
        Assert.Equal("updated", (await update.Content.ReadFromJsonAsync<JsonObject>())!["metadata"]!["topic"]!.GetValue<string>());
        using HttpResponseMessage add = await client.PostAsJsonAsync(route + "/items", new { items = new[] { new { role = "user", content = "hello" } } });
        Assert.Equal("msg_one", (await add.Content.ReadFromJsonAsync<JsonObject>())!["first_id"]!.GetValue<string>());
        await client.GetFromJsonAsync<JsonObject>(route + "/items?order=asc&limit=1&after=msg_zero&include%5B%5D=reasoning.encrypted_content");
        JsonObject? item = await client.GetFromJsonAsync<JsonObject>(route + "/items/msg_one");
        Assert.Equal("msg_one", item!["id"]!.GetValue<string>());
        using HttpResponseMessage removeItem = await client.DeleteAsync(route + "/items/msg_one");
        Assert.Equal(id, (await removeItem.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<string>());
        using HttpResponseMessage remove = await client.DeleteAsync(route);
        JsonObject deleted = (await remove.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(id, deleted["id"]!.GetValue<string>());
        Assert.True(deleted["deleted"]!.GetValue<bool>());
        Assert.Contains(_upstream.LogEntries, e => e.RequestMessage!.Url!.Contains("after=msg_zero"));
        Assert.All(_upstream.LogEntries, e => Assert.Contains("upstream-key", e.RequestMessage!.Headers!["Authorization"].ToString()));
        Assert.Contains(_upstream.LogEntries, e => e.RequestMessage!.Body?.Contains("updated") == true);
    }

    [Fact]
    public async Task Turns_PreserveOrderedReasoningToolCallsAndResults_AndCountAllPages()
    {
        await using RelayAppFactory app = new(_upstream, [new("dep")]);
        using HttpClient client = app.CreateOpenAIClient();
        string id = await CreateAsync(client);
        History();
        const string output = """[{"id":"rs_one","type":"reasoning","summary":[],"encrypted_content":"cipher"},{"id":"fc_one","type":"function_call","call_id":"call_one","name":"lookup","arguments":"{}"}]""";
        Stub("POST", Upstream.ResponsesPath, $$"""{"id":"resp_one","object":"response","status":"completed","conversation":{"id":"conv_native"},"output":{{output}}}""");
        using HttpResponseMessage first = await Turn(client, id, JsonValue.Create("lookup")!);
        JsonObject firstBody = (await first.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(id, firstBody["conversation"]!["id"]!.GetValue<string>());
        Assert.Equal("cipher", firstBody["output"]![0]!["encrypted_content"]!.GetValue<string>());
        Assert.Equal("call_one", firstBody["output"]![1]!["call_id"]!.GetValue<string>());

        JsonArray history = new(new JsonObject { ["id"] = "msg_one", ["type"] = "message", ["role"] = "user", ["content"] = "lookup" });
        foreach (JsonNode? item in (JsonArray)JsonNode.Parse(output)!) history.Add(item!.DeepClone());
        _upstream.Given(Request.Create().WithPath(Path + "/conv_native/items").UsingGet()
            .WithParam("after", new WildcardMatcher("*"))).AtPriority(-1)
            .RespondWith(Response.Create().WithHeader("Content-Type", "application/json")
                .WithBody($$"""{"data":{{output}},"has_more":false,"last_id":"fc_one"}"""));
        Stub("GET", Path + "/conv_native/items", """{"data":[{"id":"msg_one","type":"message","role":"user","content":"lookup"}],"has_more":true,"last_id":"msg_one"}""");
        JsonArray results = new(new JsonObject { ["type"] = "function_call_output", ["call_id"] = "call_one", ["output"] = "found" });
        using HttpResponseMessage second = await Turn(client, id, results.DeepClone(), true);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var calls = _upstream.LogEntries.Where(e => e.RequestMessage!.Path == Upstream.ResponsesPath).ToArray();
        JsonObject forwarded = (JsonObject)JsonNode.Parse(calls.Last().RequestMessage!.Body!)!;
        Assert.Equal("conv_native", forwarded["conversation"]!["id"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(results, forwarded["input"]));
        Assert.Equal(2, calls.Length);

        TokenEstimator estimator = app.Services.GetRequiredService<TokenEstimator>();
        long expected = estimator.CountPromptTokens(new JsonObject
        {
            ["input"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "lookup" }),
        }) + 100;
        foreach (JsonNode? item in results) history.Add(item!.DeepClone());
        expected += estimator.CountPromptTokens(new JsonObject { ["input"] = history }) + 100;
        var status = await client.GetFromJsonAsync<JsonArray>("/relay/dashboard/status");
        Assert.Equal(expected, status![0]!["reservedTokens"]!.GetValue<long>());
        Assert.Equal(2, status[0]!["requestsInWindow"]!.GetValue<int>());
    }

    [Fact]
    public async Task Routing_SurvivesAnotherInstanceRestartAndConfigurationReordering()
    {
        using WireMockServer other = WireMockServer.Start();
        TestEndpoint home = new("home", _upstream, [new("home-dep", Priority: 1)]);
        TestEndpoint backup = new("backup", other, [new("backup-dep", Priority: 2)]);
        string id;
        await using (RelayAppFactory first = new([home, backup]))
        {
            using HttpClient client = first.CreateOpenAIClient();
            id = await CreateAsync(client);
        }
        History();
        _upstream.RespondOk("home-dep");
        await using RelayAppFactory restarted = new([backup with { Deployments = [new("backup-dep", Priority: 0)] }, home]);
        using HttpClient second = restarted.CreateOpenAIClient();
        using HttpResponseMessage response = await Turn(second, id, JsonValue.Create("next")!);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(other.LogEntries);
        Assert.Contains(_upstream.LogEntries, e => e.IsFor("home-dep"));
    }

    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    [InlineData(400)]
    public async Task StatefulFailure_NeverRetriesAnotherDeploymentOrResource(int status)
    {
        using WireMockServer other = WireMockServer.Start();
        await using RelayAppFactory app = new([
            new("home", _upstream, [new("dep", Priority: 1), new("same-resource", Priority: 2)]),
            new("backup", other, [new("backup", Priority: 3)])]);
        using HttpClient client = app.CreateOpenAIClient();
        string id = await CreateAsync(client);
        History();
        Stub("POST", Upstream.ResponsesPath, """{"error":{"message":"failure","type":"invalid_request_error","code":"test"}}""", status);
        using HttpResponseMessage response = await Turn(client, id, JsonValue.Create("next")!);
        Assert.Equal(status >= 500 ? HttpStatusCode.BadGateway : (HttpStatusCode)status, response.StatusCode);
        Assert.Single(_upstream.LogEntries, e => e.RequestMessage!.Path == Upstream.ResponsesPath);
        Assert.Empty(other.LogEntries);
        if (status == 429) Assert.NotNull(response.Headers.RetryAfter);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HistoryBudget_RejectsOversizedHistoryOrExhaustedRpm(bool oversized)
    {
        await using RelayAppFactory app = new(_upstream,
            [new("dep", TokensPerMinute: 1000, RequestsPerMinute: 1)],
            settings => settings["Relay:MaxQueueWaitSeconds"] = "0");
        using HttpClient client = app.CreateOpenAIClient();
        string id = await CreateAsync(client);
        string text = oversized ? string.Join(" ", Enumerable.Repeat("history", 2000)) : "short";
        History(new JsonArray(new JsonObject { ["role"] = "user", ["content"] = text }).ToJsonString());
        _upstream.RespondOk("dep");
        using HttpResponseMessage first = await Turn(client, id, JsonValue.Create("next")!);
        if (oversized)
        {
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, first.StatusCode);
            Assert.DoesNotContain(_upstream.LogEntries, e => e.RequestMessage!.Path == Upstream.ResponsesPath);
        }
        else
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            using HttpResponseMessage second = await Turn(client, id, JsonValue.Create("again")!);
            Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
            Assert.Single(_upstream.LogEntries, e => e.RequestMessage!.Path == Upstream.ResponsesPath);
        }
    }

    [Theory]
    [InlineData("GET", "/v1/conversations/invalid")]
    [InlineData("POST", "/v1/conversations")]
    [InlineData("POST", "/v1/conversations/invalid")]
    [InlineData("DELETE", "/v1/conversations/invalid")]
    [InlineData("GET", "/v1/conversations/invalid/items")]
    [InlineData("POST", "/v1/conversations/invalid/items")]
    [InlineData("GET", "/v1/conversations/invalid/items/item")]
    [InlineData("DELETE", "/v1/conversations/invalid/items/item")]
    public async Task AllManagementRoutes_RequireBearerAuthentication(string method, string path)
    {
        await using RelayAppFactory app = new(_upstream, [new("dep")]);
        using HttpClient client = app.CreateOpenAIClient(null);
        using HttpRequestMessage request = new(new HttpMethod(method), path) { Content = JsonContent.Create(new { }) };
        using HttpResponseMessage response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("authentication_error", (await response.Content.ReadFromJsonAsync<JsonObject>())!["error"]!["type"]!.GetValue<string>());
        Assert.Empty(_upstream.LogEntries);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("conv_missing")]
    [InlineData("conv_router_000000000000000000000000_conv_native")]
    public async Task InvalidIds_ReturnOpenAI404WithoutProbingResources(string id)
    {
        await using RelayAppFactory app = new(_upstream, [new("dep")]);
        using HttpClient client = app.CreateOpenAIClient();
        using HttpResponseMessage get = await client.GetAsync("/v1/conversations/" + id);
        using HttpResponseMessage turn = await Turn(client, id, JsonValue.Create("next")!);
        foreach (HttpResponseMessage response in new[] { get, turn })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("not_found", (await response.Content.ReadFromJsonAsync<JsonObject>())!["error"]!["code"]!.GetValue<string>());
        }
        Assert.Empty(_upstream.LogEntries);
    }

    [Fact]
    public async Task DeletedConversationAndMissingItem_PreserveUpstreamErrors()
    {
        await using RelayAppFactory app = new(_upstream, [new("dep")]);
        using HttpClient client = app.CreateOpenAIClient();
        string id = await CreateAsync(client);
        const string error = """{"error":{"message":"No such item","type":"invalid_request_error","param":"item_id","code":"not_found"}}""";
        Stub("GET", Path + "/conv_native/items/missing", error, 404);
        Stub("GET", Path + "/conv_native/items", error.Replace("item_id", "conversation_id"), 404);
        using HttpResponseMessage item = await client.GetAsync($"/v1/conversations/{id}/items/missing");
        Assert.Equal(HttpStatusCode.NotFound, item.StatusCode);
        Assert.Equal("item_id", (await item.Content.ReadFromJsonAsync<JsonObject>())!["error"]!["param"]!.GetValue<string>());
        using HttpResponseMessage turn = await Turn(client, id, JsonValue.Create("next")!);
        Assert.Equal(HttpStatusCode.NotFound, turn.StatusCode);
        Assert.DoesNotContain(_upstream.LogEntries, e => e.RequestMessage!.Path == Upstream.ResponsesPath);
    }

    [Fact]
    public async Task Stream_RewritesConversationIdAndPreservesCiphertext()
    {
        await using RelayAppFactory app = new(_upstream, [new("dep")]);
        using HttpClient client = app.CreateOpenAIClient();
        string id = await CreateAsync(client);
        History();
        Stub("POST", Upstream.ResponsesPath, "");
        _upstream.Given(Request.Create().WithPath(Upstream.ResponsesPath).UsingPost())
            .RespondWith(Response.Create().WithHeader("Content-Type", "text/event-stream").WithBody(
                "data: {\"type\":\"response.completed\",\"response\":{\"conversation\":{\"id\":\"conv_native\"},\"output\":[{\"id\":\"rs_one\",\"type\":\"reasoning\",\"encrypted_content\":\"cipher\",\"summary\":[]}]}}\n\n"));
        using HttpResponseMessage response = await client.PostAsJsonAsync("/v1/responses", new { model = "auto", input = "next", conversation = id, stream = true });
        string stream = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(id, stream);
        Assert.Contains("cipher", stream);
        Assert.DoesNotContain("\"id\":\"conv_native\"", stream);
    }

    [Theory]
    [InlineData("not-json", "application/json")]
    [InlineData("[]", "application/json")]
    [InlineData("{}", "text/plain")]
    [InlineData("{\"items\":[{\"type\":\"item_reference\",\"id\":\"msg_one\"}]}", "application/json")]
    public async Task InvalidManagementBodies_ReturnOpenAI400(string body, string contentType)
    {
        await using RelayAppFactory app = new(_upstream, [new("dep")]);
        using HttpClient client = app.CreateOpenAIClient();
        using HttpResponseMessage response = await client.PostAsync("/v1/conversations",
            new StringContent(body, System.Text.Encoding.UTF8, contentType));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request_error", (await response.Content.ReadFromJsonAsync<JsonObject>())!["error"]!["type"]!.GetValue<string>());
        Assert.Empty(_upstream.LogEntries);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"data\":[],\"has_more\":true,\"last_id\":null}")]
    [InlineData("{\"data\":[],\"has_more\":true,\"last_id\":\"repeated\"}")]
    public async Task InvalidHistoryPages_StopBeforeInference(string page)
    {
        await using RelayAppFactory app = new(_upstream, [new("dep")]);
        using HttpClient client = app.CreateOpenAIClient();
        string id = await CreateAsync(client);
        Stub("GET", Path + "/conv_native/items", page);
        using HttpResponseMessage response = await Turn(client, id, JsonValue.Create("next")!);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.DoesNotContain(_upstream.LogEntries, e => e.RequestMessage!.Path == Upstream.ResponsesPath);
    }

    [Fact]
    public async Task HistoryFailure_PreservesRetryAfterAndDoesNotRunInference()
    {
        await using RelayAppFactory app = new(_upstream, [new("dep")]);
        using HttpClient client = app.CreateOpenAIClient();
        string id = await CreateAsync(client);
        _upstream.Given(Request.Create().WithPath(Path + "/conv_native/items").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(429).WithHeader("Content-Type", "application/json")
                .WithHeader("Retry-After", "17").WithBody("""{"error":{"message":"rate limited","type":"rate_limit_error","code":"rate_limit_exceeded"}}"""));
        using HttpResponseMessage response = await Turn(client, id, JsonValue.Create("next")!);
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(17), response.Headers.RetryAfter!.Delta);
        Assert.DoesNotContain(_upstream.LogEntries, e => e.RequestMessage!.Path == Upstream.ResponsesPath);
    }

    [Fact]
    public async Task ResourceReplacement_DoesNotReuseAnExistingConversationId()
    {
        string id;
        await using (RelayAppFactory first = new(_upstream, [new("dep")]))
        {
            using HttpClient client = first.CreateOpenAIClient();
            id = await CreateAsync(client);
        }
        using WireMockServer replacement = WireMockServer.Start();
        await using RelayAppFactory app = new(replacement, [new("dep")]);
        using HttpClient second = app.CreateOpenAIClient();
        using HttpResponseMessage response = await Turn(second, id, JsonValue.Create("next")!);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(replacement.LogEntries);
    }
}
