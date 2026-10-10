using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using TPMRouter.Api.Options;
using TPMRouter.Api.Endpoints;
using TPMRouter.Api.Relay;

namespace TPMRouter.Api.Auth;

public enum ApiKeyScope
{
    Client,
    Admin,
}

public sealed record ApiKeyRequirement(ApiKeyScope Scope, bool OpenAICompatible = false);

public sealed class ClientApiKeyMiddleware(RequestDelegate next, IOptions<RelayOptions> options)
{
    public const string HeaderName = "X-Api-Key";

    private readonly RelayOptions _options = options.Value;

    public Task InvokeAsync(HttpContext context)
    {
        ApiKeyRequirement? requirement = context.GetEndpoint()?.Metadata.GetMetadata<ApiKeyRequirement>();
        if (requirement is null)
        {
            return next(context);
        }

        string expected = requirement.Scope == ApiKeyScope.Admin ? _options.AdminApiKey : _options.ClientApiKey;
        if (requirement.OpenAICompatible)
        {
            string authorization = context.Request.Headers.Authorization.ToString();
            if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                || !KeysEqual(authorization[7..].Trim(), expected))
            {
                return OpenAIError.Result(new RelayOutcome(401, Error: "Invalid API key.")).ExecuteAsync(context);
            }

            return next(context);
        }

        if (!context.Request.Headers.TryGetValue(HeaderName, out var provided) || !KeysEqual(provided.ToString(), expected))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }

        return next(context);
    }

    // Hash first so the comparison time does not depend on key length.
    private static bool KeysEqual(string provided, string expected) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(provided)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
}
