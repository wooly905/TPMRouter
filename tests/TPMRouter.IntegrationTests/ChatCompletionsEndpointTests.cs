using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace TPMRouter.IntegrationTests;

public sealed class ChatCompletionsEndpointTests : IDisposable
{
    private const string RequestBody = """{"model":"auto","messages":[{"role":"user","content":"Hi"}]}""";
    private const string StreamBody = """{"model":"auto","messages":[{"role":"user","content":"Hi"}],"stream":true,"stream_options":{"include_usage":true}}""";
    private const string FirstChunk = """data: {"id":"chatcmpl-stream","object":"chat.completion.chunk","created":1700000000,"model":"test-model","choices":[{"index":0,"delta":{"role":"assistant","content":"hello"},"finish_reason":null}]}""" + "\n\n";
    private const string FinalChunks = """data: {"id":"chatcmpl-stream","object":"chat.completion.chunk","created":1700000000,"model":"test-model","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""" + "\n\n"
        + """data: {"id":"chatcmpl-stream","object":"chat.completion.chunk","created":1700000000,"model":"test-model","choices":[],"usage":{"prompt_tokens":12,"completion_tokens":3,"total_tokens":15}}""" + "\n\n"
        + "data: [DONE]\n\n";

    private readonly WireMockServer _upstream = WireMockServer.Start();

    public void Dispose() => _upstream.Stop();

    [Fact]
    public async Task Post_PreservesMessagesOptionsAndCompleteResponse()
    {
        string responseBody = """
            {"id":"chatcmpl-tools","object":"chat.completion","created":1700000000,"model":"test-model",
             "choices":[{"index":0,"message":{"role":"assistant","content":null,"refusal":null,
               "tool_calls":[{"id":"call-2","type":"function","function":{"name":"lookup","arguments":"{\"q\":\"again\"}"}}]},"finish_reason":"tool_calls","logprobs":null},
               {"index":1,"message":{"role":"assistant","content":"second"},"finish_reason":"stop"}],
             "usage":{"prompt_tokens":20,"completion_tokens":4,"total_tokens":24,"completion_tokens_details":{"reasoning_tokens":2}},"extension":"preserved"}
            """;
        Respond("dep-a", responseBody);
        await using RelayAppFactory app = new(_upstream, [new("dep-a")]);
        using HttpClient client = CreateClient(app);
        string body = """
            {"model":"auto","messages":[{"role":"developer","content":"Be helpful."},
             {"role":"user","content":[{"type":"text","text":"Find it."}]},
             {"role":"assistant","content":null,"tool_calls":[{"id":"call-1","type":"function","function":{"name":"lookup","arguments":"{}"}}]},
             {"role":"tool","tool_call_id":"call-1","content":"result"}],
             "tools":[{"type":"function","function":{"name":"lookup","parameters":{"type":"object","properties":{"q":{"type":"string"}}}}}],
             "tool_choice":"auto","parallel_tool_calls":true,"temperature":0.5,"top_p":0.9,"n":2,
             "max_completion_tokens":50,"response_format":{"type":"json_schema","json_schema":{"name":"answer","strict":true,"schema":{"type":"object"}}},
             "metadata":{"client":"test"}}
            """;

        using HttpResponseMessage response = await PostAsync(client, body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(responseBody, await response.Content.ReadAsStringAsync());
        using JsonDocument expected = JsonDocument.Parse(body);
        using JsonDocument actual = JsonDocument.Parse(_upstream.LogEntries.Single().RequestMessage!.Body!);
        Assert.Equal("dep-a", actual.RootElement.GetProperty("model").GetString());
        foreach (JsonProperty property in expected.RootElement.EnumerateObject().Where(p => p.Name != "model"))
        {
            Assert.True(JsonElement.DeepEquals(property.Value, actual.RootElement.GetProperty(property.Name)), property.Name);
        }
    }

    [Fact]
    public async Task OfficialSdk_ReadsNonStreamingCompletion()
    {
        _upstream.RespondOk("dep-a", "hello");
        await using RelayAppFactory app = new(_upstream, [new("dep-a")]);
        using HttpClient transport = app.CreateClient();
        ChatClient client = CreateSdkClient(transport);

        ChatCompletion completion = await client.CompleteChatAsync([new UserChatMessage("Hi")]);

        Assert.Equal("hello", completion.Content[0].Text);
        Assert.Equal("chatcmpl-1", completion.Id);
        Assert.Equal(15, completion.Usage.TotalTokenCount);
    }

    [Theory]
    [InlineData("{\"role\":\"assistant\",\"content\":[{\"type\":\"refusal\",\"refusal\":\"Cannot answer.\"}]}")]
    [InlineData("{\"role\":\"function\",\"name\":\"lookup\",\"content\":null}")]
    public async Task TextConversation_PreservesRefusalAndLegacyFunctionMessages(string message)
    {
        _upstream.RespondOk("dep-a");
        await using RelayAppFactory app = new(_upstream, [new("dep-a")]);
        using HttpClient client = CreateClient(app);
        string body = "{\"model\":\"auto\",\"messages\":[" + message + "]}";

        using HttpResponseMessage response = await PostAsync(client, body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument expected = JsonDocument.Parse(message);
        using JsonDocument actual = JsonDocument.Parse(_upstream.LogEntries.Single().RequestMessage!.Body!);
        Assert.True(JsonElement.DeepEquals(expected.RootElement, actual.RootElement.GetProperty("messages")[0]));
    }

    [Fact]
    public async Task OfficialSdk_ReadsStreamAndUsage()
    {
        Respond("dep-a", FirstChunk + FinalChunks, contentType: "text/event-stream");
        await using RelayAppFactory app = new(_upstream, [new("dep-a")]);
        using HttpClient transport = app.CreateClient();
        ChatClient client = CreateSdkClient(transport);
        StringBuilder text = new();
        int? totalTokens = null;

        await foreach (StreamingChatCompletionUpdate update in client.CompleteChatStreamingAsync([new UserChatMessage("Hi")]))
        {
            foreach (ChatMessageContentPart part in update.ContentUpdate)
            {
                text.Append(part.Text);
            }

            totalTokens = update.Usage?.TotalTokenCount ?? totalTokens;
        }

        Assert.Equal("hello", text.ToString());
        Assert.Equal(15, totalTokens);
    }

    [Fact]
    public async Task Stream_PreservesChunksAndUsageOptions()
    {
        Respond("dep-a", FirstChunk + FinalChunks, contentType: "text/event-stream");
        await using RelayAppFactory app = new(_upstream, [new("dep-a")]);
        using HttpClient client = CreateClient(app);

        using HttpResponseMessage response = await PostAsync(client, StreamBody);

        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(FirstChunk + FinalChunks, await response.Content.ReadAsStringAsync());
        using JsonDocument request = JsonDocument.Parse(_upstream.LogEntries.Single().RequestMessage!.Body!);
        Assert.True(request.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Bearer wrong-key")]
    [InlineData("Bearer test-admin-key")]
    [InlineData("Basic test-client-key")]
    public async Task Authentication_ReturnsStructured401(string? authorization)
    {
        await using RelayAppFactory app = new(_upstream, [new("dep-a")]);
        using HttpClient client = app.CreateClient();
        if (authorization is not null)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authorization);
        }

        using HttpResponseMessage response = await PostAsync(client, RequestBody);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("authentication_error", body.GetProperty("error").GetProperty("type").GetString());
        Assert.Equal("invalid_api_key", body.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("error").GetProperty("param").ValueKind);
        Assert.Empty(_upstream.LogEntries);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"model\":\"auto\",\"messages\":[]}")]
    [InlineData("{\"model\":\"auto\",\"messages\":[{\"role\":\"unknown\",\"content\":\"hi\"}]}")]
    [InlineData("{\"model\":\"auto\",\"messages\":[{\"role\":\"user\",\"content\":null}]}")]
    [InlineData("{\"model\":\"auto\",\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"image_url\",\"image_url\":{\"url\":\"https://example.invalid/image\"}}]}]}")]
    [InlineData("{\"model\":\"auto\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}],\"stream\":\"true\"}")]
    [InlineData("{\"model\":\"auto\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}],\"n\":0}")]
    [InlineData("{\"model\":\"auto\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}],\"max_completion_tokens\":-1}")]
    [InlineData("{\"model\":\"auto\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}],\"max_tokens\":10,\"max_completion_tokens\":10}")]
    [InlineData("{\"model\":\"auto\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}],\"modalities\":[\"audio\"]}")]
    public async Task InvalidRequest_ReturnsStructured400WithoutUpstream(string request)
    {
        await using RelayAppFactory app = new(_upstream, [new("dep-a")]);
        using HttpClient client = CreateClient(app);

        using HttpResponseMessage response = await PostAsync(client, request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_request_error", body.GetProperty("error").GetProperty("type").GetString());
        Assert.True(body.GetProperty("error").TryGetProperty("message", out _));
        Assert.True(body.GetProperty("error").TryGetProperty("param", out _));
        Assert.True(body.GetProperty("error").TryGetProperty("code", out _));
        Assert.Empty(_upstream.LogEntries);
    }

    [Fact]
    public async Task WrongContentType_ReturnsStructuredError()
    {
        await using RelayAppFactory app = new(_upstream, [new("dep-a")]);
        using HttpClient client = CreateClient(app);
        using HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", new StringContent(RequestBody));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Content-Type", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UnknownModel_Returns404WithoutUpstream()
    {
        await using RelayAppFactory app = new(_upstream, [new("dep-a")]);
        using HttpClient client = CreateClient(app);

        using HttpResponseMessage response = await PostAsync(client, RequestBody.Replace("auto", "unknown"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("model_not_found", body.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(_upstream.LogEntries);
    }

    [Fact]
    public async Task ExplicitModel_DoesNotFailOverToDifferentModel()
    {
        Respond("dep-a", """{"error":{"message":"limited","code":"upstream_limit"}}""", 429);
        _upstream.RespondOk("dep-b");
        await using RelayAppFactory app = new(_upstream, [new("dep-a"), new("dep-b")]);
        using HttpClient client = CreateClient(app);

        using HttpResponseMessage response = await PostAsync(client, RequestBody.Replace("auto", "dep-a"));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);
        Assert.Single(_upstream.LogEntries);
        Assert.True(_upstream.LogEntries.Single().IsFor("dep-a"));
    }

    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    public async Task Auto_FailsOverBeforeResponseStarts(int status)
    {
        Respond("dep-a", """{"error":{"message":"unavailable"}}""", status);
        _upstream.RespondOk("dep-b", "backup");
        await using RelayAppFactory app = new(_upstream, [new("dep-a", Priority: 1), new("dep-b", Priority: 2)]);
        using HttpClient client = CreateClient(app);

        using HttpResponseMessage response = await PostAsync(client, RequestBody);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("backup", await response.Content.ReadAsStringAsync());
        Assert.Single(_upstream.LogEntries, e => e.IsFor("dep-a"));
        Assert.Single(_upstream.LogEntries, e => e.IsFor("dep-b"));
    }

    [Fact]
    public async Task Upstream400_PreservesErrorDetailsWithoutRetry()
    {
        Respond("dep-a", """{"error":{"message":"temperature unsupported","type":"invalid_request_error","param":"temperature","code":"unsupported_value"}}""", 400);
        _upstream.RespondOk("dep-b");
        await using RelayAppFactory app = new(_upstream, [new("dep-a", Priority: 1), new("dep-b", Priority: 2)]);
        using HttpClient client = CreateClient(app);

        using HttpResponseMessage response = await PostAsync(client, RequestBody);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("temperature", body.GetProperty("error").GetProperty("param").GetString());
        Assert.Equal("unsupported_value", body.GetProperty("error").GetProperty("code").GetString());
        Assert.Single(_upstream.LogEntries);
    }

    [Fact]
    public async Task UpstreamErrorWithoutType_IsNormalized()
    {
        Respond("dep-a", """{"error":{"message":"invalid","code":"azure_error"}}""", 400);
        await using RelayAppFactory app = new(_upstream, [new("dep-a")]);
        using HttpClient client = CreateClient(app);

        using HttpResponseMessage response = await PostAsync(client, RequestBody);

        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_request_error", body.GetProperty("error").GetProperty("type").GetString());
        Assert.Equal("azure_error", body.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("error").GetProperty("param").ValueKind);
    }

    [Fact]
    public async Task OutputLimitAboveCap_IsRejectedWithoutClamping()
    {
        await using RelayAppFactory app = new(_upstream, [new("dep-a", MaxOutputTokens: 100)]);
        using HttpClient client = CreateClient(app);
        string body = RequestBody[..^1] + ",\"max_completion_tokens\":101}";

        using HttpResponseMessage response = await PostAsync(client, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_upstream.LogEntries);
    }

    [Theory]
    [InlineData("max_tokens")]
    [InlineData("max_completion_tokens")]
    public async Task RequestedLimitAndChoices_AreUsedInReservation(string field)
    {
        _upstream.RespondOk("dep-a");
        await using RelayAppFactory app = new(_upstream, [new("dep-a", MaxOutputTokens: 100)]);
        using HttpClient client = CreateClient(app);
        string body = RequestBody[..^1] + $",\"{field}\":20,\"n\":3}}";

        using HttpResponseMessage response = await PostAsync(client, body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument upstream = JsonDocument.Parse(_upstream.LogEntries.Single().RequestMessage!.Body!);
        Assert.Equal(20, upstream.RootElement.GetProperty(field).GetInt32());
        Assert.False(upstream.RootElement.TryGetProperty(field == "max_tokens" ? "max_completion_tokens" : "max_tokens", out _));
        JsonElement status = await app.CreateRelayClient(RelayAppFactory.AdminKey).GetFromJsonAsync<JsonElement>("/relay/status");
        Assert.InRange(status[0].GetProperty("reservedTokens").GetInt64(), 61, 99);
    }

    [Fact]
    public async Task TooManyChoices_ExceedBudgetBeforeUpstream()
    {
        await using RelayAppFactory app = new(_upstream, [new("dep-a", TokensPerMinute: 1000, MaxOutputTokens: 100)]);
        using HttpClient client = CreateClient(app);

        using HttpResponseMessage response = await PostAsync(client, RequestBody[..^1] + ",\"n\":10}");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(_upstream.LogEntries);
    }

    [Fact]
    public async Task HistoryAndToolSchema_AreCountedInBudget()
    {
        await using RelayAppFactory app = new(_upstream, [new("dep-a", TokensPerMinute: 1000, MaxOutputTokens: 100)]);
        using HttpClient client = CreateClient(app);
        JsonObject body = (JsonObject)JsonNode.Parse(RequestBody)!;
        ((JsonArray)body["messages"]!).Insert(0, new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = string.Concat(Enumerable.Repeat("previous answer ", 300)),
        });
        body["tools"] = JsonNode.Parse("""[{"type":"function","function":{"name":"lookup","description":"","parameters":{"type":"object"}}}]""");
        body["tools"]![0]!["function"]!["description"] = string.Concat(Enumerable.Repeat("tool description ", 300));

        using HttpResponseMessage response = await PostAsync(client, body.ToJsonString());

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(_upstream.LogEntries);
    }

    [Fact]
    public async Task InputSizeLimit_IncludesToolDefinitions()
    {
        await using RelayAppFactory app = new(_upstream, [new("dep-a")], s => s["Relay:MaxInputCharacters"] = "100");
        using HttpClient client = CreateClient(app);
        string body = RequestBody[..^1] + ",\"tools\":[{\"type\":\"function\",\"function\":{\"name\":\"lookup\",\"description\":\"" + new string('a', 100) + "\"}}]}";

        using HttpResponseMessage response = await PostAsync(client, body);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(_upstream.LogEntries);
    }

    [Fact]
    public async Task Auto_SkipsDeploymentWithInsufficientOutputCap()
    {
        _upstream.RespondOk("small");
        _upstream.RespondOk("large");
        await using RelayAppFactory app = new(_upstream, [new("small", MaxOutputTokens: 20, Priority: 1), new("large", MaxOutputTokens: 100, Priority: 2)]);
        using HttpClient client = CreateClient(app);

        using HttpResponseMessage response = await PostAsync(client, RequestBody[..^1] + ",\"max_completion_tokens\":50}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(_upstream.LogEntries);
        Assert.True(_upstream.LogEntries.Single().IsFor("large"));
    }

    [Fact]
    public async Task Stream_PreservesToolCallArgumentFragments()
    {
        string chunks = """data: {"id":"chatcmpl-tools","object":"chat.completion.chunk","created":1700000000,"model":"test-model","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call-1","type":"function","function":{"name":"lookup","arguments":"{\"q\":"}}]},"finish_reason":null}]}""" + "\n\n"
            + """data: {"id":"chatcmpl-tools","object":"chat.completion.chunk","created":1700000000,"model":"test-model","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"hello\"}"}}]},"finish_reason":null}]}""" + "\n\n"
            + """data: {"id":"chatcmpl-tools","object":"chat.completion.chunk","created":1700000000,"model":"test-model","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}""" + "\n\n"
            + "data: [DONE]\n\n";
        Respond("dep-a", chunks, contentType: "text/event-stream");
        await using RelayAppFactory app = new(_upstream, [new("dep-a")]);
        using HttpClient transport = app.CreateClient();
        ChatClient client = CreateSdkClient(transport);
        StringBuilder arguments = new();
        string? name = null;

        await foreach (StreamingChatCompletionUpdate update in client.CompleteChatStreamingAsync([new UserChatMessage("Hi")]))
        {
            foreach (StreamingChatToolCallUpdate call in update.ToolCallUpdates)
            {
                name = call.FunctionName ?? name;
                arguments.Append(call.FunctionArgumentsUpdate);
            }
        }

        Assert.Equal("lookup", name);
        Assert.Equal("{\"q\":\"hello\"}", arguments.ToString());
    }

    [Fact]
    public async Task Stream429_FailsOverAndPreservesSse()
    {
        Respond("dep-a", """{"error":{"message":"limited"}}""", 429);
        Respond("dep-b", FirstChunk + FinalChunks, contentType: "text/event-stream");
        await using RelayAppFactory app = new(_upstream, [new("dep-a", Priority: 1), new("dep-b", Priority: 2)]);
        using HttpClient client = CreateClient(app);

        using HttpResponseMessage response = await PostAsync(client, StreamBody);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(FirstChunk + FinalChunks, await response.Content.ReadAsStringAsync());
        Assert.Equal(2, _upstream.LogEntries.Count());
    }

    [Fact]
    public async Task StreamFailureAfterFirstChunk_AbortsWithoutFailover()
    {
        Respond("dep-a", FirstChunk, contentType: "text/event-stream");
        Respond("dep-b", FirstChunk + FinalChunks, contentType: "text/event-stream");
        await using RelayAppFactory app = new(_upstream, [new("dep-a", Priority: 1), new("dep-b", Priority: 2)]);
        using HttpClient client = CreateClient(app);

        Exception? failure = await Record.ExceptionAsync(async () =>
        {
            using HttpResponseMessage response = await PostAsync(client, StreamBody);
            await response.Content.ReadAsStringAsync();
        });

        Assert.IsType<HttpRequestException>(failure);
        Assert.Single(_upstream.LogEntries);
        Assert.True(_upstream.LogEntries.Single().IsFor("dep-a"));
    }

    [Fact]
    public async Task Stream_FirstChunkArrivesBeforeUpstreamCompletes()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using WebApplication upstream = builder.Build();
        upstream.MapPost(Upstream.ChatPath, async (HttpContext context) =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync(FirstChunk, context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            await release.Task.WaitAsync(context.RequestAborted);
            await context.Response.WriteAsync(FinalChunks, context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            // Keep the upstream connection open to verify that [DONE] completes the relay immediately.
            await Task.Delay(Timeout.Infinite, context.RequestAborted);
        });
        await upstream.StartAsync();
        await using RelayAppFactory app = new(_upstream, [new("dep-a")], s => s["Relay:Endpoints:0:Endpoint"] = upstream.Urls.Single());
        using HttpClient client = CreateClient(app);
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Post, "/v1/chat/completions")
            {
                Content = new StringContent(StreamBody, Encoding.UTF8, "application/json"),
            };
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            using StreamReader reader = new(await response.Content.ReadAsStreamAsync(timeout.Token));
            string? first = await reader.ReadLineAsync(timeout.Token);

            Assert.Contains("hello", first);
            Assert.False(release.Task.IsCompleted);
            release.SetResult();
            Assert.Contains("[DONE]", await reader.ReadToEndAsync(timeout.Token));
        }
        finally
        {
            release.TrySetResult();
            await upstream.StopAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamCancellationOrTimeout_ClosesUpstreamWithoutFailover(bool cancelClient)
    {
        TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using WebApplication upstream = builder.Build();
        upstream.MapPost(Upstream.ChatPath, async (HttpContext context) =>
        {
            Interlocked.Increment(ref calls);
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync(FirstChunk, context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            try
            {
                await Task.Delay(Timeout.Infinite, context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                closed.TrySetResult();
            }
        });
        await upstream.StartAsync();
        await using RelayAppFactory app = new(_upstream, [new("dep-a", Priority: 1), new("dep-b", Priority: 2)], s =>
        {
            s["Relay:Endpoints:0:Endpoint"] = upstream.Urls.Single();
            s["Relay:UpstreamTimeoutSeconds"] = cancelClient ? "10" : "1";
            s["Relay:RequestDeadlineSeconds"] = cancelClient ? "20" : "2";
        });
        using HttpClient client = CreateClient(app);
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Post, "/v1/chat/completions")
            {
                Content = new StringContent(StreamBody, Encoding.UTF8, "application/json"),
            };
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            using StreamReader reader = new(await response.Content.ReadAsStreamAsync(timeout.Token));
            Assert.Contains("hello", await reader.ReadLineAsync(timeout.Token));
            if (cancelClient)
            {
                response.Dispose();
            }
            else
            {
                await Assert.ThrowsAsync<IOException>(() => reader.ReadToEndAsync(timeout.Token));
            }

            await closed.Task.WaitAsync(timeout.Token);
            Assert.Equal(1, Volatile.Read(ref calls));
        }
        finally
        {
            await upstream.StopAsync();
        }
    }

    private void Respond(string deployment, string body, int status = 200, string contentType = "application/json") =>
        _upstream.Given(Upstream.ChatRequest(deployment)).RespondWith(Response.Create().WithStatusCode(status)
            .WithHeader("Content-Type", contentType).WithBody(body));

    private static HttpClient CreateClient(RelayAppFactory app)
    {
        HttpClient client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", RelayAppFactory.ClientKey);
        return client;
    }

    private static ChatClient CreateSdkClient(HttpClient transport) => new("auto", new ApiKeyCredential(RelayAppFactory.ClientKey),
        new OpenAIClientOptions
        {
            Endpoint = new Uri(transport.BaseAddress!, "/v1/"),
            Transport = new HttpClientPipelineTransport(transport),
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
        });

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string body) =>
        client.PostAsync("/v1/chat/completions", new StringContent(body, Encoding.UTF8, "application/json"));
}
