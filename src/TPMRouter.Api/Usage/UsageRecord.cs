using System.Text.Json.Nodes;

namespace TPMRouter.Api.Usage;

public sealed record UsageRecord(DateTimeOffset DateTime,
                                 string SourceIp,
                                 string SourceText,
                                 JsonArray ToolsList,
                                 JsonArray InvokedTool,
                                 long? InputTokenSize,
                                 long? OutputTokenSize,
                                 string OutputText);

public sealed record UsageHistoryPage(IReadOnlyList<UsageRecord> Items,
                                      long TotalCount,
                                      int Page,
                                      int PageSize);
