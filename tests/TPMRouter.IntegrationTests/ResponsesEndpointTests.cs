using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using OpenAI.Responses;
using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

#pragma warning disable OPENAI001 // ResponsesClient is experimental in the pinned OpenAI SDK.

namespace TPMRouter.IntegrationTests;

public sealed class ResponsesEndpointTests : IDisposable
{
    private const string RequestBody = """{"model":"auto","input":"Hi"}""";
    private const string ResponseBody = """
        {"id":"resp-1","object":"response","created_at":1700000000,"status":"completed","model":"test-model",
         "output":[{"type":"message","id":"msg-1","status":"completed","role":"assistant",
         "content":[{"type":"output_text","text":"hello","annotations":[]}]}],
         "usage":{"input_tokens":12,"output_tokens":3,"total_tokens":15},"extension":"preserved"}
        """;
    private const string Tools = """[{"type":"function","name":"lookup","description":"Look up a value","parameters":{"type":"object","properties":{"q":{"type":"string"}},"required":["q"],"additionalProperties":false},"strict":true}]""";

    private readonly WireMockServer _upstream = WireMockServer.Start();

    public void Dispose() => _upstream.Stop();

    [Fact]
    public async Task Post_PreservesResponsesInputReasoningToolsAndOutput()
    {
        Respond(_upstream, "dep-a", ResponseBody);
        await using RelayAppFactory app = new(_upstream, [new("dep-a")]);
        using HttpClient client = app.CreateOpenAIClient();
        JsonObject body = (JsonObject)JsonNode.Parse("""
            {"model":"auto","instructions":"Be helpful.","input":[{"role":"user","content":[{"type":"input_text","text":"Find it"}]},
             {"type":"reasoning","id":"rs-1","summary":[],"encrypted_content":"opaque"},
             {"type":"function_call","call_id":"call-1","name":"lookup","arguments":"{\"q\":\"hello\"}"},
             {"type":"function_call_output","call_id":"call-1","output":"result"}],
             "reasoning":{"effort":"medium"},"include":["reasoning.encrypted_content"],"tool_choice":"auto",
             "parallel_tool_calls":true,"max_output_tokens":50,"store":false,
             "text":{"format":{"type":"json_schema","name":"answer","schema":{"type":"object"}}},"metadata":{"client":"test"}}
            """)!;
        body["tools"] = JsonNode.Parse(Tools);

        using HttpResponseMessage response = await PostAsync(client, body.ToJsonString());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ResponseBody, await response.Content.ReadAsStringAsync());
        JsonObject actual = (JsonObject)JsonNode.Parse(_upstream.LogEntries.Single().RequestMessage!.Body!)!;
        Assert.Equal("/openai/v1/responses", _upstream.LogEntries.Single().RequestMessage!.Path);
        Assert.Equal("dep-a", actual["model"]!.GetValue<string>());
        foreach (KeyValuePair<string, JsonNode?> property in body.Where(p => p.Key != "model"))
        {
            Assert.True(JsonNode.DeepEquals(property.Value, actual[property.Key]), property.Key);
        }
    }

    [Fact]
    public async Task OfficialSdk_ReadsResponse()
    {
        Respond(_upstream, "dep-a", ResponseBody);
        await using RelayAppFactory app = new(_upstream, [new("dep-a")]);
        using HttpClient transport = app.CreateClient();
        ResponsesClient client = CreateSdkClient(transport);
        CreateResponseOptions options = new() { Model = "auto" };
        options.InputItems.Add(ResponseItem.CreateUserMessageItem("Hi"));

        ResponseResult response = await client.CreateResponseAsync(options);

        Assert.Equal("resp-1", response.Id);
        Assert.Equal("hello", response.GetOutputText());
    }

    [Fact]
    public async Task FunctionCalling_FullHistoryRoutesAcrossEndpointsByGlobalPriority()
    {
        using WireMockServer other = WireMockServer.Start();
        const string functionResponse = """
            {"id":"resp-tool","object":"response","status":"completed","model":"tool-model",
             "output":[{"type":"function_call","id":"fc-1","call_id":"call-1","name":"lookup","arguments":"{\"q\":\"hello\"}","status":"completed"}]}
            """;
        Respond(other, "first", functionResponse);
        Respond(_upstream, "second", ResponseBody);
        await using RelayAppFactory app = new([
            new("endpoint-1", _upstream, [new("second", Priority: 10)], "key-1"),
            new("endpoint-2", other, [new("first", RequestsPerMinute: 1, Priority: 9)], "key-2")]);
        using HttpClient client = app.CreateOpenAIClient();
        JsonObject body = new()
        {
            ["model"] = "auto",
            ["input"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Look up hello" }),
            ["tools"] = JsonNode.Parse(Tools),
            ["reasoning"] = new JsonObject { ["effort"] = "medium" },
            ["store"] = false,
        };
        using HttpResponseMessage first = await PostAsync(client, body.ToJsonString());
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        JsonObject responseBody = (JsonObject)JsonNode.Parse(await first.Content.ReadAsStringAsync())!;
        foreach (JsonNode? item in (JsonArray)responseBody["output"]!)
        {
            ((JsonArray)body["input"]!).Add(item!.DeepClone());
        }

        ((JsonArray)body["input"]!).Add(new JsonObject
        {
            ["type"] = "function_call_output",
            ["call_id"] = "call-1",
            ["output"] = "hello result",
        });
        using HttpResponseMessage second = await PostAsync(client, body.ToJsonString());

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Single(other.LogEntries);
        Assert.Single(_upstream.LogEntries);
        Assert.Equal("Bearer key-2", other.LogEntries.Single().RequestMessage!.Headers!["Authorization"].Single());
        Assert.Equal("Bearer key-1", _upstream.LogEntries.Single().RequestMessage!.Headers!["Authorization"].Single());
        JsonObject forwarded = (JsonObject)JsonNode.Parse(_upstream.LogEntries.Single().RequestMessage!.Body!)!;
        Assert.True(JsonNode.DeepEquals(body["input"], forwarded["input"]));
        Assert.Null(forwarded["previous_response_id"]);
    }

    [Fact]
    public async Task OutputCap_ExcludesSmallDeploymentAndPreservesExplicitLimit()
    {
        Respond(_upstream, "large", ResponseBody);
        await using RelayAppFactory app = new(_upstream, [new("small", MaxOutputTokens: 10), new("large", MaxOutputTokens: 100, Priority: 2)]);
        using HttpClient client = app.CreateOpenAIClient();

        using HttpResponseMessage response = await PostAsync(client, """{"model":"auto","input":"Hi","max_output_tokens":50}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonObject forwarded = (JsonObject)JsonNode.Parse(_upstream.LogEntries.Single().RequestMessage!.Body!)!;
        Assert.Equal("large", forwarded["model"]!.GetValue<string>());
        Assert.Equal(50, forwarded["max_output_tokens"]!.GetValue<int>());
    }

    [Fact]
    public async Task TokenBudget_CountsResponsesInputBeforeSelectingDeployment()
    {
        Respond(_upstream, "large", ResponseBody);
        await using RelayAppFactory app = new(_upstream,
            [new("small", TokensPerMinute: 1000), new("large", TokensPerMinute: 10_000, Priority: 2)]);
        using HttpClient client = app.CreateOpenAIClient();
        JsonObject body = new()
        {
            ["model"] = "auto",
            ["input"] = string.Concat(Enumerable.Repeat("token budget ", 1000)),
            ["max_output_tokens"] = 50,
        };

        using HttpResponseMessage response = await PostAsync(client, body.ToJsonString());
        JsonElement status = await client.GetFromJsonAsync<JsonElement>("/relay/dashboard/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(_upstream.LogEntries);
        Assert.True(_upstream.LogEntries.Single().IsFor("large"));
        Assert.Equal(0, status.EnumerateArray().Single(s => s.GetProperty("name").GetString() == "small")
            .GetProperty("reservedTokens").GetInt64());
        Assert.InRange(status.EnumerateArray().Single(s => s.GetProperty("name").GetString() == "large")
            .GetProperty("reservedTokens").GetInt64(), 1001, 10_000);
    }

    [Fact]
    public async Task LargeInput_UsesRemainingTpmToRouteAcrossEndpoints()
    {
        using WireMockServer other = WireMockServer.Start();
        Respond(other, "first", ResponseBody);
        Respond(_upstream, "second", ResponseBody);
        await using RelayAppFactory app = new([
            new("endpoint-1", _upstream, [new("second", TokensPerMinute: 100_000, Priority: 10)]),
            new("endpoint-2", other, [new("first", TokensPerMinute: 40_000, Priority: 9)])],
            settings => settings["Relay:MaxQueueWaitSeconds"] = "0");
        using HttpClient client = app.CreateOpenAIClient();
        JsonObject body = new()
        {
            ["model"] = "auto",
            ["input"] = string.Concat(Enumerable.Repeat("test ", 25_000)),
            ["max_output_tokens"] = 50,
        };
        string json = body.ToJsonString();
        Assert.True(json.Length > 100_000);

        using HttpResponseMessage first = await PostAsync(client, json);
        using HttpResponseMessage second = await PostAsync(client, json);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Single(other.LogEntries);
        Assert.Single(_upstream.LogEntries);
        Assert.True(other.LogEntries.Single().IsFor("first"));
        Assert.True(_upstream.LogEntries.Single().IsFor("second"));
        JsonObject forwarded = (JsonObject)JsonNode.Parse(_upstream.LogEntries.Single().RequestMessage!.Body!)!;
        Assert.Equal(body["input"]!.GetValue<string>(), forwarded["input"]!.GetValue<string>());
    }

    [Fact]
    public async Task MissingOutputLimit_UsesDeploymentCapAndUpdatesSharedBudget()
    {
        Respond(_upstream, "dep-a", ResponseBody);
        await using RelayAppFactory app = new(_upstream, [new("dep-a", RequestsPerMinute: 1, MaxOutputTokens: 75)],
            settings => settings["Relay:MaxQueueWaitSeconds"] = "0");
        using HttpClient client = app.CreateOpenAIClient();
        using HttpResponseMessage first = await PostAsync(client, RequestBody);
        JsonObject forwarded = (JsonObject)JsonNode.Parse(_upstream.LogEntries.Single().RequestMessage!.Body!)!;
        using HttpResponseMessage second = await PostAsync(client, RequestBody);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(75, forwarded["max_output_tokens"]!.GetValue<int>());
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.NotNull(second.Headers.RetryAfter);
        Assert.Single(_upstream.LogEntries);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    public async Task RetryableError_FailsOverBeforeResponse(int status)
    {
        Respond(_upstream, "first", """{"error":{"message":"unavailable"}}""", status);
        Respond(_upstream, "second", ResponseBody);
        await using RelayAppFactory app = new(_upstream, [new("first"), new("second", Priority: 2)]);
        using HttpClient client = app.CreateOpenAIClient();

        using HttpResponseMessage response = await PostAsync(client, RequestBody);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, _upstream.LogEntries.Count());
    }

    [Fact]
    public async Task Upstream400_PreservesErrorWithoutRetry()
    {
        const string error = """{"error":{"message":"unsupported effort","type":"invalid_request_error","param":"reasoning.effort","code":"unsupported_value"}}""";
        Respond(_upstream, "first", error, 400);
        await using RelayAppFactory app = new(_upstream, [new("first"), new("second", Priority: 2)]);
        using HttpClient client = app.CreateOpenAIClient();

        using HttpResponseMessage response = await PostAsync(client, RequestBody);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(error), JsonNode.Parse(await response.Content.ReadAsStringAsync())));
        Assert.Single(_upstream.LogEntries);
    }

    [Theory]
    [InlineData("response.completed")]
    [InlineData("response.incomplete")]
    [InlineData("response.failed")]
    public async Task Stream_PreservesFunctionArgumentsAndNativeTerminalEvent(string terminal)
    {
        string chunks = "event: response.function_call_arguments.delta\ndata: {\"type\":\"response.function_call_arguments.delta\",\"delta\":\"{\\\"q\\\":\"}\n\n"
            + "event: response.function_call_arguments.done\ndata: {\"type\":\"response.function_call_arguments.done\",\"arguments\":\"{\\\"q\\\":\\\"hello\\\"}\"}\n\n"
            + $"event: {terminal}\ndata: {{\"type\":\"{terminal}\",\"response\":{{\"id\":\"resp-1\"}}}}\n\n";
        Respond(_upstream, "dep-a", chunks, contentType: "text/event-stream");
        await using RelayAppFactory app = new(_upstream, [new("dep-a")]);
        using HttpClient client = app.CreateOpenAIClient();

        using HttpResponseMessage response = await PostAsync(client, """{"model":"auto","input":"Hi","stream":true}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(chunks, await response.Content.ReadAsStringAsync());
        Assert.Single(_upstream.LogEntries);
    }

    [Fact]
    public async Task Stream429_FailsOverBeforeFirstEvent()
    {
        const string chunks = "event: response.completed\ndata: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp-1\"}}\n\n";
        Respond(_upstream, "first", """{"error":{"message":"limited"}}""", 429);
        Respond(_upstream, "second", chunks, contentType: "text/event-stream");
        await using RelayAppFactory app = new(_upstream, [new("first"), new("second", Priority: 2)]);
        using HttpClient client = app.CreateOpenAIClient();

        using HttpResponseMessage response = await PostAsync(client, """{"model":"auto","input":"Hi","stream":true}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(chunks, await response.Content.ReadAsStringAsync());
        Assert.Equal(2, _upstream.LogEntries.Count());
    }

    [Fact]
    public async Task StreamWithoutTerminalEvent_AbortsWithoutFailover()
    {
        const string chunks = "event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"delta\":\"hello\"}\n\n";
        Respond(_upstream, "first", chunks, contentType: "text/event-stream");
        Respond(_upstream, "second", ResponseBody);
        await using RelayAppFactory app = new(_upstream, [new("first"), new("second", Priority: 2)]);
        using HttpClient client = app.CreateOpenAIClient();

        Exception? failure = await Record.ExceptionAsync(async () =>
        {
            using HttpResponseMessage response = await PostAsync(client, """{"model":"auto","input":"Hi","stream":true}""");
            await response.Content.ReadAsStringAsync();
        });

        Assert.IsType<HttpRequestException>(failure);
        Assert.Single(_upstream.LogEntries);
        Assert.True(_upstream.LogEntries.Single().IsFor("first"));
    }

    [Fact]
    public async Task Stream_FlushesBeforeCompletionAndStopsAtNativeTerminalEvent()
    {
        const string firstEvent = "event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"delta\":\"hello\"}\n\n";
        const string finalEvent = "event: response.completed\ndata: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp-1\"}}\n\n";
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using WebApplication upstream = builder.Build();
        upstream.MapPost("/openai/v1/responses", async (HttpContext context) =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync(firstEvent, context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            await release.Task.WaitAsync(context.RequestAborted);
            await context.Response.WriteAsync(finalEvent, context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            await Task.Delay(Timeout.Infinite, context.RequestAborted);
        });
        await upstream.StartAsync();
        await using RelayAppFactory app = new(_upstream, [new("dep-a")],
            settings => settings["Relay:Endpoints:0:Endpoint"] = upstream.Urls.Single());
        using HttpClient client = app.CreateOpenAIClient();
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Post, "/v1/responses")
            {
                Content = new StringContent("""{"model":"auto","input":"Hi","stream":true}""", Encoding.UTF8, "application/json"),
            };
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            using StreamReader reader = new(await response.Content.ReadAsStreamAsync(timeout.Token));

            Assert.Equal("event: response.output_text.delta", await reader.ReadLineAsync(timeout.Token));
            Assert.False(release.Task.IsCompleted);
            release.SetResult();
            Assert.Contains(finalEvent, await reader.ReadToEndAsync(timeout.Token));
        }
        finally
        {
            release.TrySetResult();
            await upstream.StopAsync();
        }
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"model\":\"auto\",\"input\":\"Hi\",\"previous_response_id\":\"resp-1\"}")]
    [InlineData("{\"model\":\"auto\",\"input\":\"Hi\",\"max_output_tokens\":101}")]
    public async Task InvalidRequest_Returns400WithoutUpstream(string body)
    {
        await using RelayAppFactory app = new(_upstream, [new("dep-a")]);
        using HttpClient client = app.CreateOpenAIClient();

        using HttpResponseMessage response = await PostAsync(client, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_upstream.LogEntries);
    }

    [Fact]
    public async Task MissingBearerKey_Returns401WithoutUpstream()
    {
        await using RelayAppFactory app = new(_upstream, [new("dep-a")]);
        using HttpClient client = app.CreateOpenAIClient(null);

        using HttpResponseMessage response = await PostAsync(client, RequestBody);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_upstream.LogEntries);
    }

    private static ResponsesClient CreateSdkClient(HttpClient transport) =>
        new(new ApiKeyCredential(RelayAppFactory.ClientKey), new ResponsesClientOptions
        {
            Endpoint = new Uri(transport.BaseAddress!, "/v1/"),
            Transport = new HttpClientPipelineTransport(transport),
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
        });

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string body) =>
        client.PostAsync("/v1/responses", new StringContent(body, Encoding.UTF8, "application/json"));

    private static void Respond(WireMockServer server, string deployment, string body,
                                int status = 200, string contentType = "application/json") =>
        server.Given(Request.Create().WithPath("/openai/v1/responses").UsingPost()
            .WithBody(new JsonPartialMatcher($$"""{"model":"{{deployment}}"}""")))
            .RespondWith(Response.Create().WithStatusCode(status).WithHeader("Content-Type", contentType).WithBody(body));
}
