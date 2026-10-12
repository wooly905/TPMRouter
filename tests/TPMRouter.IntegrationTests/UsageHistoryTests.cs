using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TPMRouter.Api.Usage;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace TPMRouter.IntegrationTests;

public sealed class UsageHistoryTests : IDisposable
{
    private readonly WireMockServer _upstream = WireMockServer.Start();

    public void Dispose() => _upstream.Stop();

    [Fact]
    public async Task ChatRequest_PersistsUsageAndSurvivesStoreRecreation()
    {
        _upstream.RespondOk("dep-a", "你好");
        await using RelayAppFactory app = new(_upstream, [new("dep-a")]);
        using HttpClient client = app.CreateChatClient();
        using HttpResponseMessage response = await client.PostChatAsync("台北天氣？");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        UsageHistoryStore reopened = new(app.Services.GetRequiredService<IWebHostEnvironment>(),
                                         app.Services.GetRequiredService<IConfiguration>());
        UsageHistoryPage history = reopened.GetHistory(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1), 1, 50);
        UsageRecord record = Assert.Single(history.Items);
        Assert.Equal("台北天氣？", record.SourceText);
        Assert.Equal("你好", record.OutputText);
        Assert.Equal(12, record.InputTokenSize);
        Assert.Equal(3, record.OutputTokenSize);
    }

    [Theory]
    [InlineData("/v1/chat/completions", "{\"model\":\"auto\",\"messages\":[{\"role\":\"user\",\"content\":\"Hi\"}],\"stream\":true}",
        "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"hello\"}}]}\n\ndata: {\"choices\":[],\"usage\":{\"prompt_tokens\":12,\"completion_tokens\":3}}\n\ndata: [DONE]\n\n")]
    [InlineData("/v1/responses", "{\"model\":\"auto\",\"input\":\"Hi\",\"stream\":true}",
        "event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"output_index\":0,\"delta\":\"hello\"}\n\nevent: response.completed\ndata: {\"type\":\"response.completed\",\"response\":{\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"hello\"}]}],\"usage\":{\"input_tokens\":12,\"output_tokens\":3}}}\n\n")]
    public async Task StreamingRequest_PreservesSseAndRecordsActualUsage(string path, string body, string stream)
    {
        _upstream.Given(Request.Create().WithPath($"/openai{path}").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "text/event-stream").WithBody(stream));
        await using RelayAppFactory app = new(_upstream, [new("dep-a")]);
        using HttpClient client = app.CreateChatClient();

        using HttpResponseMessage response = await client.PostAsync(path, new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(stream, await response.Content.ReadAsStringAsync());
        UsageRecord record = Assert.Single(ReadHistory(app).Items);
        Assert.Equal("hello", record.OutputText);
        Assert.Equal(12, record.InputTokenSize);
        Assert.Equal(3, record.OutputTokenSize);
        if (path == "/v1/chat/completions")
        {
            Assert.Contains("\"include_usage\":true", _upstream.LogEntries.Single().RequestMessage!.Body!);
        }
    }

    [Fact]
    public async Task ResponsesToolCall_RecordsDefinitionsCallsAndText()
    {
        const string body = """
            {"model":"auto","input":"Search Taipei","tools":[{"type":"function","name":"search","parameters":{"type":"object"}}]}
            """;
        const string completion = """
            {"id":"resp-1","output":[{"type":"function_call","call_id":"call-1","name":"search","arguments":"{}"}],
             "usage":{"input_tokens":20,"output_tokens":5}}
            """;
        _upstream.Given(Request.Create().WithPath("/openai/v1/responses").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(completion));
        await using RelayAppFactory app = new(_upstream, [new("dep-a")]);
        using HttpClient client = app.CreateChatClient();

        using HttpResponseMessage response = await client.PostAsync("/v1/responses", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        UsageRecord record = Assert.Single(ReadHistory(app).Items);
        Assert.Equal("Search Taipei", record.SourceText);
        Assert.Equal("search", record.ToolsList[0]!["name"]!.GetValue<string>());
        Assert.Equal("search", record.InvokedTool[0]!["name"]!.GetValue<string>());
        Assert.Equal(20, record.InputTokenSize);
        Assert.Equal(5, record.OutputTokenSize);
    }

    [Fact]
    public async Task ConcurrentRequests_SaveEveryRecordOnce()
    {
        _upstream.RespondOk("dep-a");
        await using RelayAppFactory app = new(_upstream, [new("dep-a")]);
        using HttpClient client = app.CreateChatClient();

        await Task.WhenAll(Enumerable.Range(0, 12).Select(async i =>
        {
            using HttpResponseMessage response = await client.PostChatAsync($"request-{i}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }));

        UsageHistoryPage history = ReadHistory(app);
        Assert.Equal(12, history.TotalCount);
        Assert.Equal(12, history.Items.Select(record => record.SourceText).Distinct().Count());
    }

    [Fact]
    public async Task InvalidAuthorizedRequest_RecordsErrorWithoutInventedUsage()
    {
        await using RelayAppFactory app = new(_upstream, [new("dep-a")]);
        using HttpClient client = app.CreateChatClient();

        using HttpResponseMessage response = await client.PostAsync("/v1/responses", new StringContent("not-json", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        UsageRecord record = Assert.Single(ReadHistory(app).Items);
        Assert.Equal("not-json", record.SourceText);
        Assert.Equal("The request body is not valid JSON.", record.OutputText);
        Assert.Null(record.InputTokenSize);
        Assert.Null(record.OutputTokenSize);
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData(RelayAppFactory.ClientKey, HttpStatusCode.Unauthorized)]
    [InlineData(RelayAppFactory.AdminKey, HttpStatusCode.OK)]
    public async Task History_RequiresAdminKey(string? key, HttpStatusCode expected)
    {
        await using RelayAppFactory app = new(_upstream, [new("dep-a")]);
        using HttpClient client = app.CreateRelayClient(key);

        using HttpResponseMessage response = await client.GetAsync("/relay/usage-history");

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(0, ReadHistory(app).TotalCount);
    }

    [Fact]
    public async Task UnauthorizedInferenceRequest_RecordsErrorWithoutCallingUpstream()
    {
        await using RelayAppFactory app = new(_upstream, [new("dep-a")]);
        using HttpClient client = app.CreateChatClient(null);

        using HttpResponseMessage response = await client.PostChatAsync("Hi");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        UsageRecord record = Assert.Single(ReadHistory(app).Items);
        Assert.Equal("Hi", record.SourceText);
        Assert.Equal("Invalid API key.", record.OutputText);
        Assert.Empty(_upstream.LogEntries);
    }

    [Fact]
    public async Task History_DefaultsToSevenDaysAndCustomRangeUsesExclusiveEnd()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero));
        await using RelayAppFactory app = new(_upstream, [new("dep-a")], services: services => services.AddSingleton<TimeProvider>(time));
        using HttpClient client = app.CreateRelayClient(RelayAppFactory.AdminKey);
        UsageHistoryStore store = app.Services.GetRequiredService<UsageHistoryStore>();
        DateTimeOffset start = time.GetUtcNow().AddDays(-7);
        store.Save(Record(start.AddMilliseconds(-1), "before"));
        store.Save(Record(start, "start"));
        store.Save(Record(start.AddDays(1), "inside"));
        store.Save(Record(time.GetUtcNow(), "end"));

        UsageHistoryPage history = (await client.GetFromJsonAsync<UsageHistoryPage>("/relay/usage-history"))!;

        Assert.Equal(2, history.TotalCount);
        Assert.Equal(new[] { "inside", "start" }, history.Items.Select(record => record.SourceText));
        string url = $"/relay/usage-history?start={Uri.EscapeDataString(start.AddDays(-1).ToString("O"))}&end={Uri.EscapeDataString(start.ToString("O"))}";
        UsageHistoryPage custom = (await client.GetFromJsonAsync<UsageHistoryPage>(url))!;
        Assert.Equal("before", Assert.Single(custom.Items).SourceText);
    }

    [Fact]
    public async Task History_PaginatesWithStableOrderingForEqualTimestamps()
    {
        await using RelayAppFactory app = new(_upstream, [new("dep-a")]);
        using HttpClient client = app.CreateRelayClient(RelayAppFactory.AdminKey);
        UsageHistoryStore store = app.Services.GetRequiredService<UsageHistoryStore>();
        DateTimeOffset timestamp = DateTimeOffset.UtcNow.AddMinutes(-1);
        for (int i = 0; i < 51; i++) store.Save(Record(timestamp, i.ToString()));

        UsageHistoryPage first = (await client.GetFromJsonAsync<UsageHistoryPage>("/relay/usage-history"))!;
        UsageHistoryPage second = (await client.GetFromJsonAsync<UsageHistoryPage>("/relay/usage-history?page=2"))!;

        Assert.Equal(51, first.TotalCount);
        Assert.Equal(50, first.Items.Count);
        Assert.Equal("50", first.Items[0].SourceText);
        Assert.Equal("0", Assert.Single(second.Items).SourceText);
    }

    [Theory]
    [InlineData("?start=2026-10-14T00:00:00Z&end=2026-10-13T00:00:00Z")]
    [InlineData("?start=2026-10-13T00:00:00Z&end=2026-10-13T00:00:00Z")]
    [InlineData("?start=invalid")]
    [InlineData("?page=0")]
    public async Task History_InvalidRangeOrPageReturns400(string query)
    {
        await using RelayAppFactory app = new(_upstream, [new("dep-a")]);
        using HttpClient client = app.CreateRelayClient(RelayAppFactory.AdminKey);

        using HttpResponseMessage response = await client.GetAsync($"/relay/usage-history{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task HistoryPage_IsLinkedFromIndexAndContainsDateFilters()
    {
        await using RelayAppFactory app = new(_upstream, [new("dep-a")]);
        using HttpClient client = app.CreateClient();

        string index = await client.GetStringAsync("/index.html");
        string page = await client.GetStringAsync("/usage-history.html");

        Assert.Contains("href=\"./usage-history.html\">使用量歷史</a>", index);
        Assert.Contains("id=\"start-date\" type=\"date\"", page);
        Assert.Contains("id=\"end-date\" type=\"date\"", page);
    }

    private static UsageHistoryPage ReadHistory(RelayAppFactory app) =>
        app.Services.GetRequiredService<UsageHistoryStore>()
            .GetHistory(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1), 1, 50);

    private static UsageRecord Record(DateTimeOffset timestamp, string source) =>
        new(timestamp, "192.0.2.1", source, [], [], null, null, "");
}
