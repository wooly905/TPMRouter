using System.Globalization;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using TPMRouter.Api.Options;

namespace TPMRouter.Api.Budget;

/// <summary>
/// Shared budget for many relay instances. Each operation is one Lua script, so selection and reservation are atomic.
/// </summary>
public sealed class RedisTokenBudgetTracker : ITokenBudgetTracker
{
    // Reads the window state of one deployment. Reservation members are "<id>:<tokens>" with the reservation time (ms) as score.
    private const string BudgetFunction = """
        local function budget(resKey, snapKey, coolKey, now, window, safety, tpm, rpm)
          redis.call('ZREMRANGEBYSCORE', resKey, '-inf', now - window)
          local snap = redis.call('HMGET', snapKey, 'tr', 'ta', 'rr', 'ra')
          local tr, ta, rr, ra = tonumber(snap[1]), tonumber(snap[2]), tonumber(snap[3]), tonumber(snap[4])
          if ta and ta + window <= now then tr = nil; ta = nil; redis.call('HDEL', snapKey, 'tr', 'ta') end
          if ra and ra + window <= now then rr = nil; ra = nil; redis.call('HDEL', snapKey, 'rr', 'ra') end
          local members = redis.call('ZRANGE', resKey, 0, -1, 'WITHSCORES')
          local reserved, count, tokAfter, reqAfter, oldest = 0, 0, 0, 0, nil
          for j = 1, #members, 2 do
            local tokens = tonumber(string.match(members[j], ':(%d+)$'))
            local at = tonumber(members[j + 1])
            reserved = reserved + tokens
            count = count + 1
            if oldest == nil or at < oldest then oldest = at end
            if ta and at > ta then tokAfter = tokAfter + tokens end
            if ra and at > ra then reqAfter = reqAfter + 1 end
          end
          local availTok = math.floor(tpm * safety) - reserved
          if tr then availTok = math.min(availTok, tr - tokAfter) end
          local availReq = nil
          if rpm > 0 then availReq = math.floor(rpm * safety) - count end
          if rr then
            local h = rr - reqAfter
            if availReq == nil then availReq = h else availReq = math.min(availReq, h) end
          end
          local cool = tonumber(redis.call('GET', coolKey)) or 0
          return { reserved = reserved, count = count, oldest = oldest, availTok = availTok, availReq = availReq, tr = tr, ta = ta, rr = rr, ra = ra, cool = cool }
        end
        """;

    // ARGV: now, window, safety, id, then per candidate: name, estimate, tpm, rpm, priority. KEYS: res, snap, cool per candidate.
    private const string ReserveScript = BudgetFunction + """

        local now, window, safety, id = tonumber(ARGV[1]), tonumber(ARGV[2]), tonumber(ARGV[3]), ARGV[4]
        local n = (#ARGV - 4) / 5
        local best, bestName, bestEst, bestPrio, bestRatio, bestTok = nil, nil, nil, nil, nil, nil
        local nextChange = nil
        local function consider(at)
          if at and at > now and (nextChange == nil or at < nextChange) then nextChange = at end
        end
        for i = 0, n - 1 do
          local a = 5 + i * 5
          local name, est, tpm, rpm, prio = ARGV[a], tonumber(ARGV[a + 1]), tonumber(ARGV[a + 2]), tonumber(ARGV[a + 3]), tonumber(ARGV[a + 4])
          local k = i * 3
          local b = budget(KEYS[k + 1], KEYS[k + 2], KEYS[k + 3], now, window, safety, tpm, rpm)
          local inCool = b.cool > now
          if b.oldest then consider(b.oldest + window) end
          if inCool then consider(b.cool) end
          if b.ta then consider(b.ta + window) end
          if b.ra then consider(b.ra + window) end
          if not inCool and b.availTok >= est and (b.availReq == nil or b.availReq >= 1) then
            local ratio = b.availTok / tpm
            if rpm > 0 and b.availReq ~= nil then ratio = math.min(ratio, b.availReq / rpm) end
            if best == nil or prio < bestPrio or (prio == bestPrio and (ratio > bestRatio or (ratio == bestRatio and b.availTok > bestTok))) then
              best, bestName, bestEst, bestPrio, bestRatio, bestTok = i, name, est, prio, ratio, b.availTok
            end
          end
        end
        if best ~= nil then
          local resKey = KEYS[best * 3 + 1]
          redis.call('ZADD', resKey, now, id .. ':' .. bestEst)
          redis.call('PEXPIRE', resKey, window * 2)
          return { bestName, bestEst }
        end
        if nextChange == nil then return { '', -1 } end
        return { '', nextChange - now }
        """;

    // ARGV: now, window, safety, then per deployment: tpm, rpm. KEYS: res, snap, cool per deployment.
    private const string StatusScript = BudgetFunction + """

        local now, window, safety = tonumber(ARGV[1]), tonumber(ARGV[2]), tonumber(ARGV[3])
        local n = (#ARGV - 3) / 2
        local result = {}
        for i = 0, n - 1 do
          local k = i * 3
          local b = budget(KEYS[k + 1], KEYS[k + 2], KEYS[k + 3], now, window, safety, tonumber(ARGV[4 + i * 2]), tonumber(ARGV[5 + i * 2]))
          local function s(v) if v == nil then return '' end return tostring(v) end
          table.insert(result, s(b.reserved))
          table.insert(result, s(b.count))
          table.insert(result, s(b.availTok))
          table.insert(result, s(b.availReq))
          table.insert(result, s(b.tr))
          table.insert(result, s(b.rr))
          table.insert(result, s(b.cool))
        end
        return result
        """;

    // ARGV: receivedAt, remainingTokens or '', remainingRequests or '', window. KEYS: snap.
    private const string RecordHeadersScript = """
        local at = tonumber(ARGV[1])
        if ARGV[2] ~= '' then
          local cur = tonumber(redis.call('HGET', KEYS[1], 'ta'))
          if cur == nil or cur < at then redis.call('HSET', KEYS[1], 'tr', ARGV[2], 'ta', ARGV[1]) end
        end
        if ARGV[3] ~= '' then
          local cur = tonumber(redis.call('HGET', KEYS[1], 'ra'))
          if cur == nil or cur < at then redis.call('HSET', KEYS[1], 'rr', ARGV[3], 'ra', ARGV[1]) end
        end
        redis.call('PEXPIRE', KEYS[1], tonumber(ARGV[4]) * 2)
        return 1
        """;

    // ARGV: until, ttl. KEYS: cool.
    private const string CooldownScript = """
        local untilMs = tonumber(ARGV[1])
        local cur = tonumber(redis.call('GET', KEYS[1])) or 0
        if untilMs > cur then redis.call('SET', KEYS[1], ARGV[1], 'PX', tonumber(ARGV[2])) end
        return 1
        """;

    private static readonly long WindowMs = (long)InMemoryTokenBudgetTracker.Window.TotalMilliseconds;

    private readonly IDatabase _database;
    private readonly RelayOptions _options;
    private readonly Dictionary<string, DeploymentTarget> _deployments;
    private readonly TimeProvider _timeProvider;

    public RedisTokenBudgetTracker(IConnectionMultiplexer redis, IOptions<RelayOptions> options, TimeProvider timeProvider)
    {
        _database = redis.GetDatabase();
        _options = options.Value;
        _deployments = _options.GetDeploymentTargets().ToDictionary(d => d.Key, StringComparer.Ordinal);
        _timeProvider = timeProvider;
    }

    public async ValueTask<ReservationResult> TryReserveAsync(IReadOnlyDictionary<string, long> estimates)
    {
        List<RedisKey> keys = [];
        List<RedisValue> args = [Now(), WindowMs, _options.SafetyFactor, Guid.NewGuid().ToString("N")];
        foreach ((string name, long estimate) in estimates)
        {
            if (_deployments.TryGetValue(name, out DeploymentTarget? target))
            {
                DeploymentOptions deployment = target.Deployment;
                AddKeys(keys, name);
                args.AddRange([name, estimate, deployment.TokensPerMinute!.Value, deployment.RequestsPerMinute!.Value, deployment.Priority]);
            }
        }

        RedisResult[] result = (RedisResult[])(await _database.ScriptEvaluateAsync(ReserveScript, [.. keys], [.. args]))!;
        string name0 = (string)result[0]!;
        long value = (long)result[1];
        if (name0.Length > 0)
        {
            return ReservationResult.Reserved(name0, value);
        }

        return ReservationResult.Full(value < 0 ? null : TimeSpan.FromMilliseconds(value));
    }

    public async ValueTask RecordHeadersAsync(string deployment, long? remainingTokens, long? remainingRequests, DateTimeOffset receivedAt)
    {
        if (remainingTokens is null && remainingRequests is null)
        {
            return;
        }

        await _database.ScriptEvaluateAsync(
            RecordHeadersScript,
            [SnapshotKey(deployment)],
            [receivedAt.ToUnixTimeMilliseconds(), remainingTokens?.ToString(CultureInfo.InvariantCulture) ?? "", remainingRequests?.ToString(CultureInfo.InvariantCulture) ?? "", WindowMs]);
    }

    public async ValueTask SetCooldownAsync(string deployment, TimeSpan duration)
    {
        long durationMs = Math.Max(1, (long)Math.Ceiling(duration.TotalMilliseconds));
        await _database.ScriptEvaluateAsync(CooldownScript, [CooldownKey(deployment)], [Now() + durationMs, durationMs]);
    }

    public async ValueTask<IReadOnlyList<DeploymentStatus>> GetStatusAsync()
    {
        long now = Now();
        List<RedisKey> keys = [];
        List<RedisValue> args = [now, WindowMs, _options.SafetyFactor];
        DeploymentTarget[] targets = [.. _deployments.Values];
        foreach (DeploymentTarget target in targets)
        {
            DeploymentOptions deployment = target.Deployment;
            AddKeys(keys, target.Key);
            args.AddRange([deployment.TokensPerMinute!.Value, deployment.RequestsPerMinute!.Value]);
        }

        RedisResult[] result = (RedisResult[])(await _database.ScriptEvaluateAsync(StatusScript, [.. keys], [.. args]))!;
        List<DeploymentStatus> statuses = [];
        for (int i = 0; i < targets.Length; i++)
        {
            DeploymentTarget target = targets[i];
            DeploymentOptions deployment = target.Deployment;
            int o = i * 7;
            long cooldown = ParseLong(result[o + 6]) ?? 0;
            statuses.Add(new DeploymentStatus(
                target.EndpointId,
                deployment.Name,
                deployment.Model,
                deployment.Priority,
                deployment.TokensPerMinute!.Value,
                deployment.RequestsPerMinute!.Value,
                ParseLong(result[o]) ?? 0,
                (int)(ParseLong(result[o + 1]) ?? 0),
                ParseLong(result[o + 2]) ?? 0,
                ParseLong(result[o + 3]),
                ParseLong(result[o + 4]),
                ParseLong(result[o + 5]),
                cooldown > now ? DateTimeOffset.FromUnixTimeMilliseconds(cooldown) : null));
        }

        return statuses;
    }

    private long Now() => _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

    private void AddKeys(List<RedisKey> keys, string deployment)
    {
        keys.Add(ReservationsKey(deployment));
        keys.Add(SnapshotKey(deployment));
        keys.Add(CooldownKey(deployment));
    }

    // The hash tag keeps all keys in one Redis Cluster slot, as one script uses keys of many deployments.
    private string ReservationsKey(string deployment) => $"{{{_options.RedisKeyPrefix}}}:v2:{deployment}:res";

    private string SnapshotKey(string deployment) => $"{{{_options.RedisKeyPrefix}}}:v2:{deployment}:snap";

    private string CooldownKey(string deployment) => $"{{{_options.RedisKeyPrefix}}}:v2:{deployment}:cool";

    private static long? ParseLong(RedisResult value)
    {
        string? text = (string?)value;
        return string.IsNullOrEmpty(text) ? null : (long)double.Parse(text, CultureInfo.InvariantCulture);
    }
}
