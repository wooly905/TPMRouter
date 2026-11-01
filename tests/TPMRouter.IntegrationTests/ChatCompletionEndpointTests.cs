using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using OpenAI;
using OpenAI.Chat;
using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace TPMRouter.IntegrationTests;

public sealed class ChatCompletionEndpointTests : IDisposable
{
    private const string Path = "/openai/v1/chat/completions";
    private const string RequestBody = """{"model":"auto","messages":[{"role":"user","content":"Hi"}]}""";
    private const string ResponseBody = """
        {"id":"chatcmpl-1","object":"chat.completion","created":1700000000,"model":"dep-a",
         "choices":[{"index":0,"message":{"role":"assistant","content":"hello"},"finish_reason":"stop","logprobs":null}],
         "usage":{"prompt_tokens":12,"completion_tokens":3,"total_tokens":15},"extension":"preserved"}
        """;
    private readonly WireMockServer _upstream = WireMockServer.Start();

    public void Dispose() => _upstream.Stop();

    [Theory]
    [InlineData("/v1/chat/completions")]
    public async Task Post_PreservesRequestAndResponse(string path)
    {
        Respond("dep-a");
        await using RelayAppFactory app = new(_upstream, [new("dep-a")]);
        using HttpClient client = app.CreateOpenAIClient();
        JsonObject body = (JsonObject)JsonNode.Parse("""
            {"model":"auto","messages":[{"role":"developer","content":"Be helpful"},
             {"role":"user","content":"Find it"},
             {"role":"assistant","content":null,"tool_calls":[{"id":"call-1","type":"function","function":{"name":"lookup","arguments":"{}"}}]},
             {"role":"tool","tool_call_id":"call-1","content":"Found"}],
             "tools":[{"type":"function","function":{"name":"lookup","parameters":{"type":"object"},"strict":true}}],
             "tool_choice":"auto","parallel_tool_calls":true,"reasoning_effort":"medium",
             "max_completion_tokens":50,"temperature":0.5,"response_format":{"type":"json_object"},"n":2}
            """)!;

        using HttpResponseMessage response = await client.PostAsJsonAsync(path, body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ResponseBody, await response.Content.ReadAsStringAsync());
        JsonObject actual = (JsonObject)JsonNode.Parse(_upstream.LogEntries.Single().RequestMessage!.Body!)!;
        body["model"] = "dep-a";
        Assert.True(JsonNode.DeepEquals(body, actual));
        Assert.Null(actual["input"]);
    }

    [Fact]
    public async Task OfficialSdk_ReadsCompletion()
    {
        Respond("dep-a");
        await using RelayAppFactory app = new(_upstream, [new("dep-a")]);
        using HttpClient transport = app.CreateClient();
        ChatClient client = CreateSdkClient(transport);

        ChatCompletion completion = await client.CompleteChatAsync([new UserChatMessage("Hi")]);

        Assert.Equal("hello", completion.Content[0].Text);
        Assert.Equal(12, completion.Usage.InputTokenCount);
        JsonObject actual = (JsonObject)JsonNode.Parse(_upstream.LogEntries.Single().RequestMessage!.Body!)!;
        Assert.Equal(100, actual["max_completion_tokens"]!.GetValue<int>());
        Assert.Null(actual["max_output_tokens"]);
    }

    [Fact]
    public async Task OfficialSdk_ReadsStreamingCompletionAndUsage()
    {
        const string stream = "data: {\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1700000000,\"model\":\"dep-a\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"hello\"},\"finish_reason\":null}]}\n\n"
            + "data: {\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1700000000,\"model\":\"dep-a\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n"
            + "data: {\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1700000000,\"model\":\"dep-a\",\"choices\":[],\"usage\":{\"prompt_tokens\":12,\"completion_tokens\":3,\"total_tokens\":15}}\n\n"
            + "data: [DONE]\n\n";
        Respond("dep-a", stream, "text/event-stream");
        await using RelayAppFactory app = new(_upstream, [new("dep-a")]);
        using HttpClient transport = app.CreateClient();
        ChatClient client = CreateSdkClient(transport);
        StringBuilder text = new();
        int? tokens = null;

        await foreach (StreamingChatCompletionUpdate update in client.CompleteChatStreamingAsync([new UserChatMessage("Hi")]))
        {
            foreach (ChatMessageContentPart part in update.ContentUpdate)
            {
                text.Append(part.Text);
            }

            tokens = update.Usage?.TotalTokenCount ?? tokens;
        }

        Assert.Equal("hello", text.ToString());
        Assert.Equal(15, tokens);
        using HttpClient admin = app.CreateRelayClient(RelayAppFactory.AdminKey);
        JsonObject history = (await admin.GetFromJsonAsync<JsonObject>("/relay/usage-history"))!;
        Assert.Equal(12, history["items"]![0]!["inputTokenSize"]!.GetValue<int>());
        Assert.Equal("hello", history["items"]![0]!["outputText"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("{}", "model")]
    [InlineData("{\"model\":\"auto\",\"messages\":[]}", "messages")]
    [InlineData("{\"model\":\"auto\",\"messages\":[null]}", "messages[0].role")]
    [InlineData("{\"model\":\"auto\",\"messages\":[{\"role\":\"user\",\"content\":42}]}", "messages[0].content")]
    [InlineData("{\"model\":\"auto\",\"messages\":[{\"role\":\"tool\",\"content\":\"Hi\"}]}", "messages[0].tool_call_id")]
    public async Task InvalidRequest_ReturnsOpenAIError(string body, string param)
    {
        await using RelayAppFactory app = new(_upstream, [new("dep-a")]);
        using HttpClient client = app.CreateOpenAIClient();
        using HttpResponseMessage response = await PostAsync(client, body);
        JsonObject error = (await response.Content.ReadFromJsonAsync<JsonObject>())!;

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(param, error["error"]!["param"]!.GetValue<string>());
        Assert.Empty(_upstream.LogEntries);
    }

    [Theory]
    [InlineData("stream", "\"true\"")]
    [InlineData("max_tokens", "0")]
    [InlineData("max_completion_tokens", "101")]
    [InlineData("n", "1.5")]
    public async Task InvalidOption_Returns400(string field, string value)
    {
        await using RelayAppFactory app = new(_upstream, [new("dep-a")]);
        using HttpClient client = app.CreateOpenAIClient();
        JsonObject body = (JsonObject)JsonNode.Parse(RequestBody)!;
        body[field] = JsonNode.Parse(value);
        using HttpResponseMessage response = await client.PostAsJsonAsync("/v1/chat/completions", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_upstream.LogEntries);
    }

    [Theory]
    [InlineData("/v1/chat/completions")]
    public async Task MissingBearerKey_Returns401(string path)
    {
        await using RelayAppFactory app = new(_upstream, [new("dep-a")]);
        using HttpClient client = app.CreateOpenAIClient(null);
        using HttpResponseMessage response = await client.PostAsJsonAsync(path, JsonNode.Parse(RequestBody));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_upstream.LogEntries);
    }

    [Fact]
    public async Task RetryableFailure_FailsOverAcrossEndpoints()
    {
        using WireMockServer other = WireMockServer.Start();
        other.Given(Request.Create().WithPath(Path).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(429).WithHeader("Retry-After", "10"));
        Respond("dep-a");
        await using RelayAppFactory app = new([
            new("first", other, [new("dep-b", Priority: 1)]),
            new("second", _upstream, [new("dep-a", Priority: 2)])]);
        using HttpClient client = app.CreateOpenAIClient();
        using HttpResponseMessage response = await PostAsync(client, RequestBody);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(other.LogEntries);
        Assert.Single(_upstream.LogEntries);
    }

    [Fact]
    public async Task ChatAndResponses_ShareRequestBudget()
    {
        Respond("dep-a");
        _upstream.RespondOk("dep-a");
        await using RelayAppFactory app = new(_upstream, [new("dep-a", RequestsPerMinute: 1)],
                                             settings => settings["Relay:MaxQueueWaitSeconds"] = "0");
        using HttpClient client = app.CreateOpenAIClient();
        using HttpResponseMessage chat = await PostAsync(client, RequestBody);
        using HttpResponseMessage responses = await client.PostResponseAsync("Hi");

        Assert.Equal(HttpStatusCode.OK, chat.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, responses.StatusCode);
        Assert.NotNull(responses.Headers.RetryAfter);
        Assert.Single(_upstream.LogEntries);
    }

    [Fact]
    public async Task MultipleChoices_ReserveOutputForEveryChoice()
    {
        await using RelayAppFactory app = new(_upstream, [new("dep-a", TokensPerMinute: 150)]);
        using HttpClient client = app.CreateOpenAIClient();
        JsonObject body = (JsonObject)JsonNode.Parse(RequestBody)!;
        body["n"] = 2;
        using HttpResponseMessage response = await client.PostAsJsonAsync("/v1/chat/completions", body);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(_upstream.LogEntries);
    }

    [Fact]
    public async Task MaxTokens_IsForwardedWithoutAddingAnotherLimit()
    {
        Respond("dep-a");
        await using RelayAppFactory app = new(_upstream, [new("dep-a")]);
        using HttpClient client = app.CreateOpenAIClient();
        JsonObject body = (JsonObject)JsonNode.Parse(RequestBody)!;
        body["max_tokens"] = 20;
        using HttpResponseMessage response = await client.PostAsJsonAsync("/v1/chat/completions", body);
        JsonObject actual = (JsonObject)JsonNode.Parse(_upstream.LogEntries.Single().RequestMessage!.Body!)!;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(20, actual["max_tokens"]!.GetValue<int>());
        Assert.Null(actual["max_completion_tokens"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Images_RequireVisionDeployment(bool supportsVision)
    {
        Respond("dep-a");
        await using RelayAppFactory app = new(_upstream, [new("dep-a")],
                                             settings => settings["Relay:Endpoints:0:Deployments:0:SupportsVision"] = supportsVision.ToString());
        using HttpClient client = app.CreateOpenAIClient();
        using HttpResponseMessage response = await PostAsync(client, """
            {"model":"auto","messages":[{"role":"user","content":[{"type":"text","text":"Describe"},
             {"type":"image_url","image_url":{"url":"data:image/png;base64,AAAA"}}]}]}
            """);

        Assert.Equal(supportsVision ? HttpStatusCode.OK : HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(supportsVision ? 1 : 0, _upstream.LogEntries.Count());
    }

    [Fact]
    public async Task UpstreamError_PreservesErrorWithoutRetry()
    {
        const string error = """{"error":{"message":"Unsupported option","type":"invalid_request_error","param":"temperature","code":"unsupported_value"}}""";
        _upstream.Given(Request.Create().WithPath(Path).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(400).WithHeader("Content-Type", "application/json").WithBody(error));
        await using RelayAppFactory app = new(_upstream, [new("dep-a"), new("dep-b", Priority: 2)]);
        using HttpClient client = app.CreateOpenAIClient();
        using HttpResponseMessage response = await PostAsync(client, RequestBody);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(error), await response.Content.ReadFromJsonAsync<JsonObject>()));
        Assert.Single(_upstream.LogEntries);
    }

    [Theory]
    [InlineData("data: {invalid}\n\n")]
    [InlineData("data: {\"error\":{\"message\":\"failed\"}}\n\n")]
    public async Task InvalidFirstStreamEvent_FailsOverBeforeResponseStarts(string stream)
    {
        Respond("dep-a", stream, "text/event-stream");
        Respond("dep-b", "data: {\"choices\":[]}\n\ndata: [DONE]\n\n", "text/event-stream");
        await using RelayAppFactory app = new(_upstream, [new("dep-a", Priority: 1), new("dep-b", Priority: 2)]);
        using HttpClient client = app.CreateOpenAIClient();
        JsonObject body = (JsonObject)JsonNode.Parse(RequestBody)!;
        body["stream"] = true;
        using HttpResponseMessage response = await client.PostAsJsonAsync("/v1/chat/completions", body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("data: [DONE]", await response.Content.ReadAsStringAsync());
        Assert.Equal(2, _upstream.LogEntries.Count());
    }

    [Fact]
    public async Task TruncatedStream_AbortsWithoutFailover()
    {
        Respond("dep-a", "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"partial\"}}]}\n\n", "text/event-stream");
        Respond("dep-b");
        await using RelayAppFactory app = new(_upstream, [new("dep-a", Priority: 1), new("dep-b", Priority: 2)]);
        using HttpClient client = app.CreateOpenAIClient();
        using HttpRequestMessage request = new(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent(RequestBody[..^1] + ",\"stream\":true}", Encoding.UTF8, "application/json"),
        };
        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => response.Content.ReadAsStringAsync());
        Assert.Single(_upstream.LogEntries);
    }

    private void Respond(string deployment, string body = ResponseBody, string contentType = "application/json") =>
        _upstream.Given(Request.Create().WithPath(Path).UsingPost()
                .WithBody(new JsonPartialMatcher($$"""{"model":"{{deployment}}"}""")))
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", contentType).WithBody(body));

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string body) =>
        client.PostAsync("/v1/chat/completions", new StringContent(body, Encoding.UTF8, "application/json"));

    private static ChatClient CreateSdkClient(HttpClient transport) =>
        new("auto", new ApiKeyCredential(RelayAppFactory.ClientKey), new OpenAIClientOptions
        {
            Endpoint = new Uri(transport.BaseAddress!, "/v1/"),
            Transport = new HttpClientPipelineTransport(transport),
            RetryPolicy = new ClientRetryPolicy(0),
        });
}
