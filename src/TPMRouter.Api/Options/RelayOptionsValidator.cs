using Microsoft.Extensions.Options;

namespace TPMRouter.Api.Options;

public sealed class RelayOptionsValidator : IValidateOptions<RelayOptions>
{
    public ValidateOptionsResult Validate(string? name, RelayOptions options)
    {
        List<string> failures = [];

        if (string.IsNullOrWhiteSpace(options.ClientApiKey))
        {
            failures.Add("Relay:ClientApiKey is required.");
        }

        if (string.IsNullOrWhiteSpace(options.AdminApiKey))
        {
            failures.Add("Relay:AdminApiKey is required.");
        }

        if (options.BudgetStore == BudgetStore.Redis && string.IsNullOrWhiteSpace(options.RedisConnectionString))
        {
            failures.Add("Relay:RedisConnectionString is required when Relay:BudgetStore is Redis.");
        }

        if (options.SafetyFactor is <= 0 or > 1)
        {
            failures.Add("Relay:SafetyFactor must be > 0 and <= 1.");
        }

        if (options.MaxQueueWaitSeconds < 0)
        {
            failures.Add("Relay:MaxQueueWaitSeconds must be >= 0.");
        }

        if (options.ErrorCooldownSeconds < 0)
        {
            failures.Add("Relay:ErrorCooldownSeconds must be >= 0.");
        }

        if (options.UpstreamTimeoutSeconds <= 0)
        {
            failures.Add("Relay:UpstreamTimeoutSeconds must be > 0.");
        }

        if (options.RequestDeadlineSeconds <= options.UpstreamTimeoutSeconds)
        {
            failures.Add("Relay:RequestDeadlineSeconds must be > Relay:UpstreamTimeoutSeconds.");
        }

        if (options.Endpoints.Count == 0)
        {
            failures.Add("Relay:Endpoints must contain at least one endpoint.");
        }

        HashSet<string> ids = new(StringComparer.Ordinal);
        HashSet<Uri> endpoints = [];
        for (int i = 0; i < options.Endpoints.Count; i++)
        {
            EndpointOptions endpoint = options.Endpoints[i];
            string prefix = $"Relay:Endpoints:{i}";

            if (string.IsNullOrWhiteSpace(endpoint.Id))
            {
                failures.Add($"{prefix}:Id is required.");
            }
            else if (!ids.Add(endpoint.Id))
            {
                failures.Add($"{prefix}:Id '{endpoint.Id}' is a duplicate.");
            }

            if (!Uri.TryCreate(endpoint.Endpoint, UriKind.Absolute, out Uri? uri)
                || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
                || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0)
            {
                failures.Add($"{prefix}:Endpoint must be an absolute HTTP or HTTPS resource root URI.");
            }
            else if (!endpoints.Add(uri))
            {
                failures.Add($"{prefix}:Endpoint is a duplicate resource URI.");
            }

            if (string.IsNullOrWhiteSpace(endpoint.ApiKey))
            {
                failures.Add($"{prefix}:ApiKey is required.");
            }

            if (endpoint.Deployments.Count == 0)
            {
                failures.Add($"{prefix}:Deployments must contain at least one deployment.");
            }

            HashSet<string> names = new(StringComparer.Ordinal);
            for (int j = 0; j < endpoint.Deployments.Count; j++)
            {
                DeploymentOptions deployment = endpoint.Deployments[j];
                string deploymentPrefix = $"{prefix}:Deployments:{j}";
                if (string.IsNullOrWhiteSpace(deployment.Name))
                {
                    failures.Add($"{deploymentPrefix}:Name is required.");
                }
                else if (!names.Add(deployment.Name))
                {
                    failures.Add($"{deploymentPrefix}:Name '{deployment.Name}' is a duplicate within this endpoint.");
                }

                if (string.IsNullOrWhiteSpace(deployment.Model) || deployment.Model == "auto")
                {
                    failures.Add($"{deploymentPrefix}:Model is required and must not be the reserved 'auto' alias.");
                }

                if (deployment.TokensPerMinute is null or <= 0)
                {
                    failures.Add($"{deploymentPrefix}:TokensPerMinute is required and must be > 0.");
                }

                if (deployment.RequestsPerMinute is null or < 0)
                {
                    failures.Add($"{deploymentPrefix}:RequestsPerMinute is required and must be >= 0.");
                }

                if (deployment.MaxOutputTokens <= 0)
                {
                    failures.Add($"{deploymentPrefix}:MaxOutputTokens must be > 0.");
                }
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
