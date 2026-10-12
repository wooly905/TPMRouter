using TPMRouter.Api.Usage;

namespace TPMRouter.Api.Endpoints;

public static class UsageHistoryEndpoint
{
    public static IResult GetHistory(HttpContext context,
                                     UsageHistoryStore store,
                                     TimeProvider timeProvider,
                                     DateTimeOffset? start = null,
                                     DateTimeOffset? end = null,
                                     int page = 1)
    {
        DateTimeOffset endTime = end ?? timeProvider.GetUtcNow();
        DateTimeOffset startTime = start ?? endTime.AddDays(-7);

        if (startTime >= endTime || page < 1)
        {
            return Results.BadRequest(new { error = "開始時間必須早於結束時間，頁碼必須大於 0。" });
        }

        context.Response.Headers.CacheControl = "no-store";

        return Results.Ok(store.GetHistory(startTime, endTime, page, 50));
    }
}
