using TPMRouter.Api.Budget;

namespace TPMRouter.Api.Endpoints;

public static class RelayEndpoint
{
    public static async Task<IResult> GetStatus(ITokenBudgetTracker tracker) => Results.Ok(await tracker.GetStatusAsync());
}
