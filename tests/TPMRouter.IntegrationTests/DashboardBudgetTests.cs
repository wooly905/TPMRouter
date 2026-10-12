using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TPMRouter.Api.Budget;
using TPMRouter.Api.Usage;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace TPMRouter.IntegrationTests;

public sealed class DashboardBudgetTests : IDisposable
{
    private readonly WireMockServer _upstream = WireMockServer.Start();

    public void Dispose() => _upstream.Stop();

    [Theory]
    [InlineData(200)]
    [InlineData(400)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task EveryUpstreamAttempt_ReservesInputAndOutputCapRegardlessOfStatus(int upstreamStatus)
    {
        string body = upstreamStatus == 200 ? Upstream.Completion("hello") : """{"error":{"message":"failed"}}""";
        _upstream.Given(Upstream.ChatRequest("dep-a"))
            .RespondWith(Response.Create().WithStatusCode(upstreamStatus)
                .WithHeader("Content-Type", "application/json").WithBody(body));
        await using RelayAppFactory app = new(_upstream, [new("dep-a", MaxOutputTokens: 32_000)]);
        using HttpClient client = app.CreateChatClient();

        using HttpResponseMessage response = await client.PostChatAsync("Hi");
        DeploymentStatus status = Assert.Single(await StatusAsync(client));

        Assert.True(status.ReservedTokens > 32_000);
        Assert.Equal(1, status.RequestsInWindow);
        Assert.Equal(upstreamStatus == 200 ? 15 : 0, status.ActualTokens);
        UsageRecord usage = Assert.Single(app.Services.GetRequiredService<UsageHistoryStore>()
            .GetHistory(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1), 1, 50).Items);
        Assert.Equal(upstreamStatus == 200 ? 12L : (long?)null, usage.InputTokenSize);
        Assert.Equal(upstreamStatus == 200 ? 3L : (long?)null, usage.OutputTokenSize);
    }

    [Fact]
    public async Task SuccessfulRequestWithoutUsage_StillCountsTowardRateLimits()
    {
        _upstream.Given(Upstream.ChatRequest("dep-a"))
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody("""{"choices":[{"index":0,"message":{"content":"hello"}}]}"""));
        await using RelayAppFactory app = new(_upstream, [new("dep-a")]);
        using HttpClient client = app.CreateChatClient();

        using HttpResponseMessage response = await client.PostChatAsync("Hi");
        DeploymentStatus status = Assert.Single(await StatusAsync(client));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(status.ReservedTokens > 100);
        Assert.Equal(1, status.RequestsInWindow);
        Assert.Equal(0, status.ActualTokens);
    }

    [Fact]
    public async Task FailedRequestWithUsage_ContributesReportedTokens()
    {
        _upstream.Given(Upstream.ChatRequest("dep-a"))
            .RespondWith(Response.Create().WithStatusCode(400).WithHeader("Content-Type", "application/json")
                .WithBody("""{"error":{"message":"failed"},"usage":{"prompt_tokens":12,"completion_tokens":3}}"""));
        await using RelayAppFactory app = new(_upstream, [new("dep-a")]);
        using HttpClient client = app.CreateChatClient();

        using HttpResponseMessage response = await client.PostChatAsync("Hi");
        DeploymentStatus status = Assert.Single(await StatusAsync(client));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(15, status.ActualTokens);
        Assert.True(status.ReservedTokens > 100);
    }

    [Fact]
    public async Task Failover_ReservesBothFailedAndSuccessfulAttempts()
    {
        _upstream.Given(Upstream.ChatRequest("first"))
            .RespondWith(Response.Create().WithStatusCode(500).WithHeader("Content-Type", "application/json")
                .WithBody("""{"error":{"message":"failed"},"usage":{"prompt_tokens":10,"completion_tokens":2}}"""));
        _upstream.RespondOk("second");
        await using RelayAppFactory app = new(_upstream, [new("first"), new("second", Priority: 2)]);
        using HttpClient client = app.CreateChatClient();

        using HttpResponseMessage response = await client.PostChatAsync("Hi");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.All(await StatusAsync(client), status =>
        {
            Assert.True(status.ReservedTokens > 100);
            Assert.Equal(1, status.RequestsInWindow);
        });
        DeploymentStatus[] statuses = await StatusAsync(client);
        Assert.Equal(12, statuses.Single(status => status.Name == "first").ActualTokens);
        Assert.Equal(15, statuses.Single(status => status.Name == "second").ActualTokens);
    }

    [Fact]
    public async Task InterruptedChatStream_DoesNotRefundReservation()
    {
        const string stream = "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":12,\"completion_tokens\":3}}\n\n";
        _upstream.Given(Upstream.ChatRequest("dep-a"))
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "text/event-stream").WithBody(stream));
        await using RelayAppFactory app = new(_upstream, [new("dep-a")]);
        using HttpClient client = app.CreateChatClient();

        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            using HttpResponseMessage response = await client.PostAsync("/v1/chat/completions",
                new StringContent("""{"model":"auto","messages":[{"role":"user","content":"Hi"}],"stream":true}""",
                                  Encoding.UTF8, "application/json"));
        });

        DeploymentStatus status = Assert.Single(await StatusAsync(client));
        Assert.True(status.ReservedTokens > 100);
        Assert.Equal(1, status.RequestsInWindow);
        Assert.Equal(15, status.ActualTokens);
    }

    [Fact]
    public async Task FailedResponsesEvent_DoesNotRefundReservation()
    {
        const string stream = "data: {\"type\":\"response.failed\",\"response\":{\"error\":{\"message\":\"failed\"},\"usage\":{\"input_tokens\":20,\"output_tokens\":5}}}\n\n";
        _upstream.Given(Request.Create().WithPath("/openai/v1/responses").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "text/event-stream").WithBody(stream));
        await using RelayAppFactory app = new(_upstream, [new("dep-a")]);
        using HttpClient client = app.CreateChatClient();

        using HttpResponseMessage response = await client.PostAsync("/v1/responses",
            new StringContent("""{"model":"auto","input":"Hi","stream":true}""", Encoding.UTF8, "application/json"));
        DeploymentStatus status = Assert.Single(await StatusAsync(client));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(status.ReservedTokens > 100);
        Assert.Equal(1, status.RequestsInWindow);
        Assert.Equal(25, status.ActualTokens);
    }

    [Fact]
    public async Task LocalValidationFailure_DoesNotReserveUpstreamBudget()
    {
        await using RelayAppFactory app = new(_upstream, [new("dep-a")]);
        using HttpClient client = app.CreateChatClient();

        using HttpResponseMessage response = await client.PostAsync("/v1/responses",
            new StringContent("not-json", Encoding.UTF8, "application/json"));
        DeploymentStatus status = Assert.Single(await StatusAsync(client));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, status.ReservedTokens);
        Assert.Equal(0, status.RequestsInWindow);
        Assert.Empty(_upstream.LogEntries);
        Assert.Equal(0, status.ActualTokens);
    }

    [Fact]
    public async Task FailedAttempt_ExpiresAfter60Seconds()
    {
        FakeTimeProvider time = new();
        _upstream.Given(Upstream.ChatRequest("dep-a"))
            .RespondWith(Response.Create().WithStatusCode(400).WithHeader("Content-Type", "application/json")
                .WithBody("""{"error":{"message":"failed"}}"""));
        await using RelayAppFactory app = new(_upstream, [new("dep-a")],
            services: services => services.AddSingleton<TimeProvider>(time));
        using HttpClient client = app.CreateChatClient();
        using HttpResponseMessage response = await client.PostChatAsync("Hi");

        time.Advance(TimeSpan.FromSeconds(59));
        Assert.True(Assert.Single(await StatusAsync(client)).ReservedTokens > 100);
        time.Advance(TimeSpan.FromSeconds(1));
        DeploymentStatus status = Assert.Single(await StatusAsync(client));
        Assert.Equal(0, status.ReservedTokens);
        Assert.Equal(0, status.RequestsInWindow);
    }

    private static async Task<DeploymentStatus[]> StatusAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<DeploymentStatus[]>("/relay/dashboard/status"))!;

    [Theory]
    [InlineData("/v1/chat/completions", "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":12,\"completion_tokens\":3}}\n\ndata: [DONE]\n\n", 15)]
    [InlineData("/v1/responses", "data: {\"type\":\"response.completed\",\"response\":{\"usage\":{\"input_tokens\":20,\"output_tokens\":5}}}\n\n", 25)]
    public async Task SuccessfulStream_RecordsUsageOnce(string path, string stream, long expectedTokens)
    {
        _upstream.Given(Request.Create().WithPath($"/openai{path}").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "text/event-stream").WithBody(stream));
        await using RelayAppFactory app = new(_upstream, [new("dep-a")]);
        using HttpClient client = app.CreateChatClient();
        string request = path == "/v1/responses"
            ? """{"model":"auto","input":"Hi","stream":true}"""
            : """{"model":"auto","messages":[{"role":"user","content":"Hi"}],"stream":true}""";

        using HttpResponseMessage response = await client.PostAsync(path, new StringContent(request, Encoding.UTF8, "application/json"));
        DeploymentStatus status = Assert.Single(await StatusAsync(client));

        Assert.Equal(stream, await response.Content.ReadAsStringAsync());
        Assert.Equal(expectedTokens, status.ActualTokens);
        Assert.Equal(1, status.RequestsInWindow);
    }

    [Theory]
    [InlineData("/v1/chat/completions", "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":12,\"completion_tokens\":3}}\n\n")]
    [InlineData("/v1/responses", "data: {\"type\":\"response.completed\",\"response\":{\"usage\":{\"input_tokens\":12,\"output_tokens\":3}}}\n\n")]
    public async Task FailureWritingEventDelimiter_RetainsUsageAlreadyReceived(string path, string stream)
    {
        _upstream.Given(Request.Create().WithPath($"/openai{path}").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "text/event-stream").WithBody(stream));
        await using RelayAppFactory app = new(_upstream, [new("dep-a")],
            services: services => services.AddSingleton<IStartupFilter>(new DelimiterFailureFilter()));
        using HttpClient client = app.CreateChatClient();
        string request = path == "/v1/responses"
            ? """{"model":"auto","input":"Hi","stream":true}"""
            : """{"model":"auto","messages":[{"role":"user","content":"Hi"}],"stream":true}""";

        Exception? failure = await Record.ExceptionAsync(async () =>
        {
            using HttpResponseMessage response = await client.PostAsync(path,
                new StringContent(request, Encoding.UTF8, "application/json"));
        });

        Assert.NotNull(failure);
        DeploymentStatus status = Assert.Single(await StatusAsync(client));
        Assert.Equal(15, status.ActualTokens);
        Assert.Equal(1, status.RequestsInWindow);
        Assert.True(status.ReservedTokens > 100);
    }

    private sealed class DelimiterFailureFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                Stream original = context.Response.Body;
                using DelimiterFailureStream failure = new(original);
                if (context.Request.Method == "POST") context.Response.Body = failure;
                try
                {
                    await nextMiddleware(context);
                }
                finally
                {
                    context.Response.Body = original;
                }
            });
            next(app);
        };
    }

    private sealed class DelimiterFailureStream(Stream inner) : MemoryStream
    {
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Length == 1 && buffer.Span[0] == (byte)'\n')
            {
                throw new IOException("Simulated connection failure while sending the event delimiter.");
            }

            return inner.WriteAsync(buffer, cancellationToken);
        }
    }
}
