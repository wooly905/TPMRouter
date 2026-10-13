using System.ClientModel.Primitives;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using TPMRouter.Api.Budget;
using TPMRouter.Api.Options;
using TPMRouter.Api.Upstream;
using TPMRouter.Api.Usage;

namespace TPMRouter.Api.Relay;

public sealed record RelayOutcome(int StatusCode,
                                  string? Error = null,
                                  TimeSpan? RetryAfter = null,
                                  BinaryData? Content = null,
                                  string? ErrorParam = null,
                                  string? ErrorCode = null)
{
    public const int ClientClosedRequest = 499;
}

public sealed class RelayService(IOptions<RelayOptions> options,
                                 TokenEstimator estimator,
                                 ITokenBudgetTracker tracker,
                                 IUpstreamClient upstream,
                                 RelayMetrics metrics,
                                 TimeProvider timeProvider,
                                 ILogger<RelayService> logger)
{
    private readonly RelayOptions _options = options.Value;
    private readonly Dictionary<string, DeploymentTarget> _deployments =
        options.Value.GetDeploymentTargets().ToDictionary(d => d.Key, StringComparer.Ordinal);

    public async Task<RelayOutcome> RelayResponseAsync(ResponsesRequest request,
                                                       Func<PipelineResponse, CancellationToken, Task> onStream,
                                                       Func<bool> hasStarted,
                                                       CancellationToken requestAborted,
                                                       UsageRecordBuilder? streamedUsage = null)
    {
        JsonObject requestBody = request.Body;
        string model = request.Model;
        int? maxOutputTokens = request.MaxOutputTokens;
        List<DeploymentTarget> candidates = _deployments.Values
            .Where(d => model == "auto" || d.Deployment.Model == model).ToList();
        if (candidates.Count == 0)
        {
            return new RelayOutcome(404, Error: "The requested model is not available.", ErrorParam: "model", ErrorCode: "model_not_found");
        }

        candidates = candidates.Where(d => maxOutputTokens is null || maxOutputTokens <= d.Deployment.MaxOutputTokens).ToList();
        if (candidates.Count == 0)
        {
            return new RelayOutcome(400, Error: "The requested output limit exceeds the configured limit for this model.",
                                    ErrorParam: "max_output_tokens");
        }

        int promptTokens = estimator.CountPromptTokens(requestBody);
        Dictionary<string, long> estimates = new(StringComparer.Ordinal);
        foreach (DeploymentTarget target in candidates)
        {
            DeploymentOptions deployment = target.Deployment;
            long estimate = promptTokens + (maxOutputTokens ?? deployment.MaxOutputTokens);
            if (DeploymentSelector.FitsDeployment(estimate, deployment, _options.SafetyFactor))
            {
                estimates[target.Key] = estimate;
            }
        }

        if (estimates.Count == 0)
        {
            return new RelayOutcome(413, Error: "The token estimate is too large for all eligible deployments.");
        }

        return await RelayCoreAsync(estimates, async (selected, token) =>
        {
            streamedUsage?.ResetUsage();
            JsonObject body = (JsonObject)requestBody.DeepClone();
            body["model"] = selected.Deployment.Name;

            if (maxOutputTokens is null)
            {
                body["max_output_tokens"] = selected.Deployment.MaxOutputTokens;
            }

            DeploymentStatus status = (await tracker.GetStatusAsync())
                .Single(s => s.EndpointId == selected.EndpointId && s.Name == selected.Deployment.Name);
            logger.LogInformation("Token budget diagnostic: model {Model}, endpoint {EndpointId}, deployment {Deployment}, estimated input {EstimatedInputTokens}, output limit {OutputTokenLimit}, attempt reservation {ReservedTokens}, window reserved {WindowReservedTokens}, window requests {WindowRequests}, available {AvailableTokens}, effective TPM {EffectiveTokensPerMinute}.",
                                  model,
                                  selected.EndpointId,
                                  selected.Deployment.Name,
                                  promptTokens,
                                  maxOutputTokens ?? selected.Deployment.MaxOutputTokens,
                                  estimates[selected.Key],
                                  status.ReservedTokens,
                                  status.RequestsInWindow,
                                  status.AvailableTokens,
                                  (long)Math.Floor(status.TokensPerMinute * _options.SafetyFactor));

            try
            {
                UpstreamResult result = await upstream.CreateResponseAsync(selected.Key, body, async (response, streamToken) =>
                {
                    RateLimitHeaders headers = AzureOpenAIUpstreamClient.ReadRateLimits(response.Headers);
                    await tracker.RecordHeadersAsync(selected.Key, headers.RemainingTokens, headers.RemainingRequests, timeProvider.GetUtcNow());
                    await onStream(response, streamToken);
                }, token);
                if (!result.Streamed && result.Content is { } content)
                {
                    UsageRecordBuilder usage = new("");
                    usage.ReadResponse(content.ToString(), false);
                    if (usage.TotalTokens is { } actualTokens)
                    {
                        await tracker.RecordUsageAsync(selected.Key, actualTokens);
                    }
                }

                return result;
            }
            finally
            {
                if (streamedUsage?.TotalTokens is { } actualTokens)
                {
                    await tracker.RecordUsageAsync(selected.Key, actualTokens);
                }
            }
        }, hasStarted, requestAborted);
    }

    private async Task<RelayOutcome> RelayCoreAsync(Dictionary<string, long> estimates,
                                                    Func<DeploymentTarget, CancellationToken, Task<UpstreamResult>> complete,
                                                    Func<bool>? hasStarted,
                                                    CancellationToken requestAborted)
    {
        DateTimeOffset start = timeProvider.GetUtcNow();
        TimeSpan deadline = TimeSpan.FromSeconds(_options.RequestDeadlineSeconds);
        DateTimeOffset deadlineAt = start + deadline;
        DateTimeOffset queueLimitAt = start + TimeSpan.FromSeconds(Math.Min(_options.MaxQueueWaitSeconds, _options.RequestDeadlineSeconds));

        using CancellationTokenSource deadlineCts = new(deadline, timeProvider);
        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, deadlineCts.Token);
        CancellationToken token = linkedCts.Token;

        RelayOutcome? lastFailure = null;
        for (int attempt = 0; attempt < _deployments.Count && estimates.Count > 0; attempt++)
        {
            ReservationResult reservation;
            while (true)
            {
                reservation = await tracker.TryReserveAsync(estimates);
                if (reservation.Success)
                {
                    break;
                }

                DateTimeOffset now = timeProvider.GetUtcNow();
                if (reservation.RetryAfter is not { } wait || now + wait > queueLimitAt || now + wait > deadlineAt)
                {
                    if (lastFailure is not null)
                    {
                        return lastFailure;
                    }

                    logger.LogWarning("All deployments are full. Retry after {RetryAfter}.", reservation.RetryAfter);
                    return new RelayOutcome(
                        StatusCodes.Status429TooManyRequests,
                        Error: "All deployments are at their limit.",
                        RetryAfter: reservation.RetryAfter ?? TimeSpan.FromSeconds(1));
                }

                try
                {
                    await Task.Delay(wait, timeProvider, token);
                }
                catch (OperationCanceledException)
                {
                    return Cancelled(requestAborted);
                }
            }

            string deploymentName = reservation.Deployment!;
            DeploymentTarget selected = _deployments[deploymentName];
            metrics.Selected(selected, reservation.Tokens);
            logger.LogInformation("Selected deployment {Deployment} with {ReservedTokens} reserved tokens.", deploymentName, reservation.Tokens);

            TimeSpan remaining = deadlineAt - timeProvider.GetUtcNow();
            TimeSpan upstreamTimeout = TimeSpan.FromSeconds(_options.UpstreamTimeoutSeconds);
            bool limitedByDeadline = remaining <= upstreamTimeout;
            TimeSpan attemptTimeout = limitedByDeadline ? remaining : upstreamTimeout;
            if (attemptTimeout <= TimeSpan.Zero)
            {
                return Cancelled(requestAborted);
            }

            UpstreamResult result;
            using (CancellationTokenSource attemptTimeoutCts = new(attemptTimeout, timeProvider))
            using (CancellationTokenSource attemptCts = CancellationTokenSource.CreateLinkedTokenSource(token, attemptTimeoutCts.Token))
            {
                try
                {
                    result = await complete(selected, attemptCts.Token);
                }
                catch (OperationCanceledException) when (hasStarted?.Invoke() == true)
                {
                    return Cancelled(requestAborted);
                }
                catch (OperationCanceledException) when (!requestAborted.IsCancellationRequested && !deadlineCts.IsCancellationRequested && !limitedByDeadline)
                {
                    logger.LogWarning("Upstream timeout on deployment {Deployment}.", deploymentName);
                    await tracker.SetCooldownAsync(deploymentName, TimeSpan.FromSeconds(_options.ErrorCooldownSeconds));
                    estimates.Remove(deploymentName);
                    lastFailure = new RelayOutcome(StatusCodes.Status504GatewayTimeout, Error: "The upstream request timed out.");
                    continue;
                }
                catch (OperationCanceledException)
                {
                    return Cancelled(requestAborted);
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException)
                {
                    logger.LogWarning("Upstream connection failed on deployment {Deployment}.", deploymentName);
                    if (hasStarted?.Invoke() == true)
                    {
                        await tracker.SetCooldownAsync(deploymentName, TimeSpan.FromSeconds(_options.ErrorCooldownSeconds));
                        return new RelayOutcome(502, Error: "The upstream stream failed.");
                    }

                    result = new UpstreamResult(0, "The upstream connection failed.", default);
                }
            }

            if (!result.Streamed)
            {
                await tracker.RecordHeadersAsync(deploymentName, result.RateLimits.RemainingTokens, result.RateLimits.RemainingRequests, timeProvider.GetUtcNow());
            }

            logger.LogInformation("Token budget diagnostic upstream: deployment {Deployment}, status {UpstreamStatus}, remaining tokens {HeaderRemainingTokens}, remaining requests {HeaderRemainingRequests}.",
                                  deploymentName,
                                  result.Status,
                                  result.RateLimits.RemainingTokens,
                                  result.RateLimits.RemainingRequests);

            if (result.Status is >= 200 and < 300)
            {
                logger.LogInformation("Deployment {Deployment} completed.", deploymentName);
                return new RelayOutcome(
                    StatusCodes.Status200OK,
                    Content: result.Content);
            }

            if (result.Status == StatusCodes.Status429TooManyRequests)
            {
                TimeSpan cooldown = result.RateLimits.RetryAfter ?? TimeSpan.FromSeconds(_options.ErrorCooldownSeconds);
                metrics.Upstream429(selected);
                logger.LogWarning("Deployment {Deployment} returned 429. Cooldown {Cooldown}.", deploymentName, cooldown);
                await tracker.SetCooldownAsync(deploymentName, cooldown);
                estimates.Remove(deploymentName);
                lastFailure = new RelayOutcome(StatusCodes.Status429TooManyRequests, Error: "All deployments are at their limit.", RetryAfter: cooldown);
                continue;
            }

            if (result.Status is 0 or 500 or 502 or 503 or 504)
            {
                logger.LogWarning("Deployment {Deployment} returned {Status}.", deploymentName, result.Status);
                await tracker.SetCooldownAsync(deploymentName, TimeSpan.FromSeconds(_options.ErrorCooldownSeconds));
                estimates.Remove(deploymentName);
                lastFailure = new RelayOutcome(StatusCodes.Status502BadGateway, Error: "The upstream request failed.");
                continue;
            }

            logger.LogWarning("Deployment {Deployment} returned {Status}. No retry.", deploymentName, result.Status);
            return new RelayOutcome(result.Status, Error: result.ErrorMessage, Content: result.Content);
        }

        return lastFailure ?? new RelayOutcome(StatusCodes.Status502BadGateway, Error: "The upstream request failed.");
    }

    private RelayOutcome Cancelled(CancellationToken requestAborted)
    {
        if (requestAborted.IsCancellationRequested)
        {
            logger.LogInformation("The client cancelled the request.");
            return new RelayOutcome(RelayOutcome.ClientClosedRequest);
        }

        logger.LogWarning("The request deadline occurred.");
        return new RelayOutcome(StatusCodes.Status504GatewayTimeout, Error: "The request deadline occurred.");
    }
}
