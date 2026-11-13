using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Kojima.Core.Config;
using Kojima.Core.Models;
using Microsoft.Data.Sqlite;

namespace Kojima.Agents.Usage;

/// <summary>
/// 在线额度 / 余额读取。所有方法都只修改传入的 snapshot：
/// 网络或登录失败时把原因追加到 Note，绝不丢掉本机已经读到的 Token。
/// </summary>
public static class UsageQuotas
{
    private static readonly HttpClient Http = CreateHttp();
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    // ---------------------------------------------------------------- Claude

    public static async Task AddClaudeAsync(UsageSnapshot snapshot, CancellationToken ct)
    {
        try
        {
            var home = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            if (string.IsNullOrWhiteSpace(home))
            {
                home = Path.Combine(Profile, ".claude");
            }

            using var credentials = ReadJson(Path.Combine(home, ".credentials.json"));
            if (credentials is null)
            {
                Append(snapshot, "未找到 Claude 登录，只显示本机日志");
                return;
            }

            var oauth = credentials.RootElement.TryGetProperty("claudeAiOauth", out var node) ? node : default;
            var token = LocalUsageExtra.Text(oauth, "accessToken");
            if (token.Length == 0)
            {
                Append(snapshot, "Claude 登录信息缺少 accessToken");
                return;
            }

            snapshot.Plan ??= LocalUsageExtra.Text(oauth, "subscriptionType");

            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
            using var doc = await SendAsync(request, ct);
            var root = doc.RootElement;

            AddWindow(root, "five_hour", "5 小时额度", 300, snapshot);
            AddWindow(root, "seven_day", "每周额度", 10080, snapshot);

            if (root.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
            {
                foreach (var limit in limits.EnumerateArray())
                {
                    var kind = LocalUsageExtra.Text(limit, "kind");
                    var percent = LocalUsageExtra.Number(limit, "percent");
                    if (percent is null)
                    {
                        continue;
                    }

                    var label = kind switch
                    {
                        "session" => "5 小时额度",
                        "weekly_all" => "每周额度",
                        "weekly_scoped" => ScopedLabel(limit),
                        _ => "额度",
                    };

                    if (snapshot.Quotas.Any(q => q.Label == label))
                    {
                        continue;
                    }

                    snapshot.Quotas.Add(new Quota
                    {
                        Label = label,
                        UsedPercent = Math.Clamp(percent.Value, 0, 100),
                        ResetAt = DateOf(limit, "resets_at"),
                        Minutes = kind == "session" ? 300 : kind == "weekly_all" ? 10080 : null,
                    });
                }
            }

            if (snapshot.Quotas.Count == 0)
            {
                Append(snapshot, "Claude 未返回额度窗口");
            }
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            Append(snapshot, "Claude 额度：" + ex.Message);
        }
    }

    private static void AddWindow(JsonElement root, string name, string label, double minutes, UsageSnapshot snapshot)
    {
        if (!root.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (LocalUsageExtra.Number(window, "utilization") is not { } used || snapshot.Quotas.Any(q => q.Label == label))
        {
            return;
        }

        snapshot.Quotas.Add(new Quota
        {
            Label = label,
            UsedPercent = Math.Clamp(used, 0, 100),
            ResetAt = DateOf(window, "resets_at"),
            Minutes = minutes,
        });
    }

    private static string ScopedLabel(JsonElement limit)
    {
        var name = limit.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.Object
            ? LocalUsageExtra.Text(scope, "model")
            : "";
        if (name.Length == 0 && limit.TryGetProperty("scope", out var scope2) && scope2.ValueKind == JsonValueKind.Object)
        {
            name = LocalUsageExtra.Text(scope2, "display_name");
        }

        return name.Length > 0 ? name + " · 每周" : "每周额度";
    }

    // ---------------------------------------------------------------- Codex

    public static async Task AddCodexAsync(UsageSnapshot snapshot, CancellationToken ct)
    {
        try
        {
            var home = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (string.IsNullOrWhiteSpace(home))
            {
                home = Path.Combine(Profile, ".codex");
            }

            using var auth = ReadJson(Path.Combine(home, "auth.json"));
            if (auth is null)
            {
                Append(snapshot, "未找到 Codex 登录，只显示本机日志");
                return;
            }

            var tokens = auth.RootElement.TryGetProperty("tokens", out var node) ? node : default;
            var token = LocalUsageExtra.Text(tokens, "access_token");
            var account = LocalUsageExtra.Text(tokens, "account_id");
            if (token.Length == 0)
            {
                Append(snapshot, "Codex 登录信息缺少 access_token");
                return;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, "https://chatgpt.com/backend-api/wham/usage");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (account.Length > 0)
            {
                request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", account);
            }

            using var doc = await SendAsync(request, ct);
            var root = doc.RootElement;
            snapshot.Plan ??= LocalUsageExtra.Text(root, "plan_type");

            if (root.TryGetProperty("rate_limit", out var rate) && rate.ValueKind == JsonValueKind.Object)
            {
                AddCodexWindow(rate, "primary_window", snapshot);
                AddCodexWindow(rate, "secondary_window", snapshot);
            }

            if (snapshot.Quotas.Count == 0)
            {
                Append(snapshot, "Codex 未返回额度窗口");
            }
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            Append(snapshot, "Codex 额度：" + ex.Message);
        }
    }

    private static void AddCodexWindow(JsonElement rate, string name, UsageSnapshot snapshot)
    {
        if (!rate.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (LocalUsageExtra.Number(window, "used_percent") is not { } used)
        {
            return;
        }

        var seconds = LocalUsageExtra.Number(window, "limit_window_seconds") ?? 0;
        var label = seconds switch
        {
            > 0 and <= 21600 => "5 小时额度",
            >= 518400 => "每周额度",
            > 0 => $"{Math.Round(seconds / 3600)} 小时额度",
            _ => "额度",
        };

        if (snapshot.Quotas.Any(q => q.Label == label))
        {
            return;
        }

        snapshot.Quotas.Add(new Quota
        {
            Label = label,
            UsedPercent = Math.Clamp(used, 0, 100),
            ResetAt = DateOf(window, "reset_at") ?? DateFromSeconds(window, "reset_after_seconds"),
            Minutes = seconds > 0 ? seconds / 60 : null,
        });
    }

    // ---------------------------------------------------------------- Cursor

    public static async Task AddCursorAsync(UsageSnapshot snapshot, CancellationToken ct)
    {
        try
        {
            var database = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Cursor", "User", "globalStorage", "state.vscdb");
            var token = ReadCursorToken(database);
            if (token.Length == 0)
            {
                Append(snapshot, "未找到 Cursor 登录（请先打开 Cursor 并登录）");
                return;
            }

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                "https://api2.cursor.sh/aiserver.v1.DashboardService/GetCurrentPeriodUsage");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var doc = await SendAsync(request, ct);
            var root = doc.RootElement;

            snapshot.Plan ??= LocalUsageExtra.Text(root, "membershipType");
            var plan = root.TryGetProperty("planUsage", out var node) ? node : default;
            if (plan.ValueKind != JsonValueKind.Object &&
                root.TryGetProperty("individualUsage", out var individual) &&
                individual.ValueKind == JsonValueKind.Object)
            {
                plan = individual.TryGetProperty("plan", out var inner) ? inner : default;
            }

            var reset = DateOf(root, "billingCycleEnd");
            var any = false;
            foreach (var (key, label) in new[] { ("autoPercentUsed", "Cursor 模型"), ("apiPercentUsed", "其他模型") })
            {
                if (LocalUsageExtra.Number(plan, key) is not { } percent)
                {
                    continue;
                }

                snapshot.Quotas.Add(new Quota { Label = label, UsedPercent = Math.Clamp(percent, 0, 100), ResetAt = reset });
                any = true;
            }

            if (!any && LocalUsageExtra.Number(plan, "totalPercentUsed") is { } total)
            {
                snapshot.Quotas.Add(new Quota { Label = "账期额度", UsedPercent = Math.Clamp(total, 0, 100), ResetAt = reset });
                any = true;
            }

            if (!any)
            {
                Append(snapshot, "Cursor 未返回可用额度");
            }

            Append(snapshot, "Token 明细来自账户接口，包含其他设备上的用量");
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            Append(snapshot, "Cursor 额度：" + ex.Message);
        }
    }

    private static string ReadCursorToken(string database)
    {
        if (!File.Exists(database))
        {
            return "";
        }

        try
        {
            using var connection = LocalUsageExtra.Open(database);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM ItemTable WHERE key = 'cursorAuth/accessToken' LIMIT 1";
            var value = command.ExecuteScalar() as string ?? "";
            value = value.Trim();
            if (value.StartsWith('"') && value.EndsWith('"'))
            {
                value = JsonSerializer.Deserialize<string>(value) ?? "";
            }

            return value.Trim();
        }
        catch (Exception ex) when (ex is SqliteException or JsonException or IOException)
        {
            return "";
        }
    }

    // ---------------------------------------------------------------- Kimi quota

    public static async Task AddKimiAsync(UsageSnapshot snapshot, CancellationToken ct)
    {
        try
        {
            var home = Environment.GetEnvironmentVariable("KIMI_CODE_HOME");
            if (string.IsNullOrWhiteSpace(home))
            {
                home = Path.Combine(Profile, ".kimi-code");
            }

            var token = ReadKimiToken(home);
            if (token.Length == 0)
            {
                Append(snapshot, "未找到 Kimi Code 登录，只显示本机日志");
                return;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.kimi.com/coding/v1/usages");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var doc = await SendAsync(request, ct);
            var root = doc.RootElement;

            if (!root.TryGetProperty("usages", out var usages) || usages.ValueKind != JsonValueKind.Object)
            {
                Append(snapshot, "Kimi 账户没有可显示的套餐额度");
                return;
            }

            var any = false;
            foreach (var (key, label, minutes) in new[]
                     {
                         ("limit_5h", "5 小时额度", 300d),
                         ("limit_7d", "每周额度", 10080d),
                         ("limit_month_total", "每月额度", 43200d),
                     })
            {
                if (!usages.TryGetProperty(key, out var window) || window.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (LocalUsageExtra.Number(window, "used_ratio") is not { } ratio)
                {
                    continue;
                }

                snapshot.Quotas.Add(new Quota
                {
                    Label = label,
                    UsedPercent = Math.Clamp(ratio * 100, 0, 100),
                    ResetAt = DateOf(window, "reset_time"),
                    Minutes = minutes,
                });
                any = true;
            }

            if (!any)
            {
                Append(snapshot, "Kimi 未返回可用额度");
            }
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            Append(snapshot, "Kimi 额度：" + ex.Message);
        }
    }

    private static string ReadKimiToken(string home)
    {
        try
        {
            var folder = Path.Combine(home, "credentials");
            if (!Directory.Exists(folder))
            {
                return "";
            }

            foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
            {
                using var doc = ReadJson(file);
                if (doc is null)
                {
                    continue;
                }

                var token = LocalUsageExtra.Text(doc.RootElement, "access_token");
                if (token.Length > 0)
                {
                    return token;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        return "";
    }

    // ---------------------------------------------------------------- DeepSeek

    public static async Task AddDeepSeekAsync(UsageSnapshot snapshot, CancellationToken ct)
    {
        var key = SecretStore.Get(SecretNames.Usage("deepseek"));
        if (key.Length == 0)
        {
            Append(snapshot, "未填写 DeepSeek API Key");
            return;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.deepseek.com/user/balance");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var doc = await SendAsync(request, ct);
            var root = doc.RootElement;

            if (root.TryGetProperty("balance_infos", out var infos) && infos.ValueKind == JsonValueKind.Array)
            {
                foreach (var info in infos.EnumerateArray())
                {
                    var currency = LocalUsageExtra.Text(info, "currency");
                    if (LocalUsageExtra.Number(info, "total_balance") is not { } total)
                    {
                        continue;
                    }

                    snapshot.Metrics.Add(new Metric
                    {
                        Kind = MetricKind.Balance,
                        Label = "余额",
                        Amount = total,
                        Currency = currency,
                    });

                    if (LocalUsageExtra.Number(info, "granted_balance") is { } granted && granted > 0)
                    {
                        snapshot.Metrics.Add(new Metric
                        {
                            Kind = MetricKind.Balance,
                            Label = "赠送余额",
                            Amount = granted,
                            Currency = currency,
                        });
                    }
                }
            }

            if (snapshot.Metrics.Count == 0)
            {
                Append(snapshot, "DeepSeek 未返回余额");
            }
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            Append(snapshot, "DeepSeek 余额：" + ex.Message);
        }
    }

    // ---------------------------------------------------------------- OpenRouter

    public static async Task AddOpenRouterAsync(UsageSnapshot snapshot, CancellationToken ct)
    {
        var key = SecretStore.Get(SecretNames.Usage("openrouter"));
        if (key.Length == 0)
        {
            Append(snapshot, "未填写 OpenRouter API Key");
            return;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://openrouter.ai/api/v1/credits");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var doc = await SendAsync(request, ct);
            var data = doc.RootElement.TryGetProperty("data", out var node) ? node : doc.RootElement;

            if (LocalUsageExtra.Number(data, "total_credits") is { } total)
            {
                var used = LocalUsageExtra.Number(data, "total_usage") ?? 0;
                snapshot.Metrics.Add(new Metric
                {
                    Kind = MetricKind.Balance,
                    Label = "剩余额度",
                    Amount = Math.Max(0, total - used),
                    Total = total,
                    Used = used,
                    Currency = "USD",
                });
                snapshot.Metrics.Add(new Metric
                {
                    Kind = MetricKind.Spend,
                    Label = "累计花费",
                    Amount = used,
                    Currency = "USD",
                });
                return;
            }

            using var keyRequest = new HttpRequestMessage(HttpMethod.Get, "https://openrouter.ai/api/v1/key");
            keyRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var keyDoc = await SendAsync(keyRequest, ct);
            var keyData = keyDoc.RootElement.TryGetProperty("data", out var keyNode) ? keyNode : keyDoc.RootElement;

            if (LocalUsageExtra.Number(keyData, "limit_remaining") is { } remaining)
            {
                snapshot.Metrics.Add(new Metric
                {
                    Kind = MetricKind.Balance,
                    Label = "剩余额度",
                    Amount = remaining,
                    Total = LocalUsageExtra.Number(keyData, "limit"),
                    Used = LocalUsageExtra.Number(keyData, "usage"),
                    Currency = "USD",
                    ResetAt = DateOf(keyData, "limit_reset"),
                });
            }
            else if (LocalUsageExtra.Number(keyData, "usage") is { } usage)
            {
                snapshot.Metrics.Add(new Metric
                {
                    Kind = MetricKind.Spend,
                    Label = "已用",
                    Amount = usage,
                    Currency = "USD",
                });
            }

            if (snapshot.Metrics.Count == 0)
            {
                Append(snapshot, "OpenRouter 未返回额度信息");
            }
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            Append(snapshot, "OpenRouter 额度：" + ex.Message);
        }
    }

    // ---------------------------------------------------------------- GLM

    public static async Task AddGlmAsync(UsageSnapshot snapshot, CancellationToken ct)
    {
        var key = SecretStore.Get(SecretNames.Usage("glm"));
        if (key.Length == 0)
        {
            Append(snapshot, "未填写 GLM API Key");
            return;
        }

        foreach (var host in new[] { "https://open.bigmodel.cn", "https://api.z.ai" })
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, host + "/api/monitor/usage/quota/limit");
                request.Headers.TryAddWithoutValidation("Authorization", key);
                using var doc = await SendAsync(request, ct);
                var root = doc.RootElement;
                var data = root.TryGetProperty("data", out var node) ? node : root;

                snapshot.Plan ??= LocalUsageExtra.Text(data, "level");
                if (!data.TryGetProperty("limits", out var limits) || limits.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var limit in limits.EnumerateArray())
                {
                    var type = LocalUsageExtra.Text(limit, "type");
                    var percent = LocalUsageExtra.Number(limit, "percentage");
                    if (percent is null &&
                        LocalUsageExtra.Number(limit, "currentValue") is { } current &&
                        LocalUsageExtra.Number(limit, "usage") is { } total && total > 0)
                    {
                        percent = current / total * 100;
                    }

                    if (percent is null)
                    {
                        continue;
                    }

                    var label = type switch
                    {
                        "TOKENS_LIMIT" => "Token 额度",
                        "CREDIT_LIMIT" => "额度",
                        "TIME_LIMIT" => GlmWindowLabel(limit),
                        _ => "额度",
                    };

                    if (snapshot.Quotas.Any(q => q.Label == label))
                    {
                        continue;
                    }

                    snapshot.Quotas.Add(new Quota
                    {
                        Label = label,
                        UsedPercent = Math.Clamp(percent.Value, 0, 100),
                        ResetAt = EpochDate(limit, "nextResetTime"),
                    });
                }

                if (snapshot.Quotas.Count > 0)
                {
                    return;
                }
            }
            catch (Exception ex) when (IsRecoverable(ex))
            {
                Append(snapshot, "GLM 额度：" + ex.Message);
            }
        }

        Append(snapshot, "GLM 未返回可用额度");
    }

    private static string GlmWindowLabel(JsonElement limit)
    {
        var unit = LocalUsageExtra.Number(limit, "unit");
        var number = LocalUsageExtra.Number(limit, "number");
        if (unit == 3 && number is { } hours)
        {
            return hours >= 24 ? $"{(int)(hours / 24)} 天额度" : $"{(int)hours} 小时额度";
        }

        if (unit == 6)
        {
            return "每周额度";
        }

        return "额度";
    }

    // ---------------------------------------------------------------- NewAPI

    public static async Task AddNewApiAsync(UsageSnapshot snapshot, UsageSettings settings, CancellationToken ct)
    {
        var key = SecretStore.Get(SecretNames.Usage("newapi"));
        var site = NormalizeSite(settings.Sites.GetValueOrDefault("newapi") ?? "");
        if (key.Length == 0 || site.Length == 0)
        {
            Append(snapshot, "未填写 NewAPI 站点或 API Key");
            return;
        }

        try
        {
            using var doc = await NewApiUsageAsync(site, key, ct);
            var root = doc.RootElement;
            var ok = (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True) ||
                     (root.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.True);
            if (!ok)
            {
                Append(snapshot, "NewAPI 返回失败：" + ErrorMessage(root));
                return;
            }

            var data = root.TryGetProperty("data", out var node) ? node : root;
            if (data.TryGetProperty("unlimited_quota", out var unlimited) && unlimited.ValueKind == JsonValueKind.True)
            {
                snapshot.Metrics.Add(new Metric { Kind = MetricKind.Balance, Label = "额度", Unlimited = true });
                return;
            }

            if (LocalUsageExtra.Number(data, "total_available") is { } available)
            {
                snapshot.Metrics.Add(new Metric
                {
                    Kind = MetricKind.Balance,
                    Label = "剩余额度",
                    Amount = available,
                    Total = LocalUsageExtra.Number(data, "total_granted"),
                    Used = LocalUsageExtra.Number(data, "total_used"),
                    Currency = "USD",
                });
            }

            if (snapshot.Metrics.Count == 0)
            {
                Append(snapshot, "NewAPI 未返回额度字段");
            }
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            Append(snapshot, "NewAPI 额度：" + ex.Message);
        }
    }

    private static async Task<JsonDocument> NewApiUsageAsync(string site, string key, CancellationToken ct)
    {
        try
        {
            return await NewApiGetAsync(site + "/api/usage/token/", key, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException)
        {
            return await NewApiGetAsync(site + "/api/usage/token", key, ct);
        }
    }

    private static async Task<JsonDocument> NewApiGetAsync(string url, string key, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return await SendAsync(request, ct);
    }

    // ---------------------------------------------------------------- Sub2API

    public static async Task AddSub2ApiAsync(UsageSnapshot snapshot, UsageSettings settings, CancellationToken ct)
    {
        var key = SecretStore.Get(SecretNames.Usage("sub2api"));
        var site = NormalizeSite(settings.Sites.GetValueOrDefault("sub2api") ?? "");
        if (key.Length == 0 || site.Length == 0)
        {
            Append(snapshot, "未填写 Sub2API 站点或 API Key");
            return;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, site + "/v1/usage");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var doc = await SendAsync(request, ct);
            var root = doc.RootElement;
            var data = root.TryGetProperty("data", out var node) ? node : root;

            if (data.TryGetProperty("isValid", out var valid) && valid.ValueKind == JsonValueKind.False)
            {
                Append(snapshot, "Sub2API 令牌无效");
                return;
            }

            snapshot.Plan ??= LocalUsageExtra.Text(data, "planName");
            var mode = LocalUsageExtra.Text(data, "mode");

            if (data.TryGetProperty("rate_limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
            {
                foreach (var limit in limits.EnumerateArray())
                {
                    var window = LocalUsageExtra.Text(limit, "window");
                    var used = LocalUsageExtra.Number(limit, "used");
                    var total = LocalUsageExtra.Number(limit, "limit");
                    if (used is null || total is null || total <= 0)
                    {
                        continue;
                    }

                    snapshot.Quotas.Add(new Quota
                    {
                        Label = WindowLabel(window),
                        UsedPercent = Math.Clamp(used.Value / total.Value * 100, 0, 100),
                        ResetAt = DateOf(limit, "reset_at"),
                        Minutes = window switch { "5h" => 300, "1d" => 1440, "7d" => 10080, _ => null },
                    });
                }
            }

            if (data.TryGetProperty("quota", out var quota) && quota.ValueKind == JsonValueKind.Object)
            {
                var remaining = LocalUsageExtra.Number(quota, "remaining");
                var total = LocalUsageExtra.Number(quota, "limit");
                if (remaining is not null)
                {
                    snapshot.Metrics.Add(new Metric
                    {
                        Kind = MetricKind.Balance,
                        Label = "订阅余额",
                        Amount = remaining,
                        Total = total,
                        Currency = LocalUsageExtra.Text(quota, "unit"),
                    });
                }
            }

            if (data.TryGetProperty("balance", out var balance) && balance.ValueKind == JsonValueKind.Number)
            {
                snapshot.Metrics.Add(new Metric
                {
                    Kind = MetricKind.Balance,
                    Label = "账户余额",
                    Amount = balance.GetDouble(),
                    Currency = LocalUsageExtra.Text(data, "unit"),
                });
            }

            if (data.TryGetProperty("usage", out var usage) &&
                usage.ValueKind == JsonValueKind.Object &&
                usage.TryGetProperty("today", out var today) &&
                LocalUsageExtra.Number(today, "actual_cost") is { } cost)
            {
                snapshot.Metrics.Add(new Metric
                {
                    Kind = MetricKind.Spend,
                    Label = "今日花费",
                    Amount = cost,
                    Currency = LocalUsageExtra.Text(data, "unit"),
                });
            }

            if (mode == "unrestricted")
            {
                snapshot.Metrics.Add(new Metric { Kind = MetricKind.Balance, Label = "额度", Unlimited = true });
            }

            if (snapshot.Quotas.Count == 0 && snapshot.Metrics.Count == 0)
            {
                Append(snapshot, "Sub2API 未返回额度字段");
            }
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            Append(snapshot, "Sub2API 额度：" + ex.Message);
        }
    }

    private static string WindowLabel(string window) => window switch
    {
        "5h" => "5 小时额度",
        "1d" => "每日额度",
        "7d" => "每周额度",
        "" => "额度",
        _ => window + " 额度",
    };

    // ---------------------------------------------------------------- helpers

    private static string Profile =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Kojima/1.2.7");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    private static async Task<JsonDocument> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidDataException($"HTTP {(int)response.StatusCode}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, JsonOptions, ct);
    }

    private static JsonDocument? ReadJson(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return JsonDocument.Parse(stream, JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void Append(UsageSnapshot snapshot, string note)
    {
        snapshot.Note = string.IsNullOrEmpty(snapshot.Note) ? note : snapshot.Note + " · " + note;
    }

    private static bool IsRecoverable(Exception ex) =>
        ex is HttpRequestException or JsonException or IOException or UnauthorizedAccessException
            or InvalidDataException or TaskCanceledException or NotSupportedException or FormatException;

    private static string NormalizeSite(string site)
    {
        site = site.Trim().TrimEnd('/');
        if (site.Length == 0)
        {
            return "";
        }

        return site.Contains("://", StringComparison.Ordinal) ? site : "https://" + site;
    }

    private static string ErrorMessage(JsonElement root)
    {
        var message = LocalUsageExtra.Text(root, "message");
        return message.Length > 0 ? message : "未知错误";
    }

    private static DateTimeOffset? DateOf(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => ParseDate(value.GetString()),
            JsonValueKind.Number when value.TryGetInt64(out var number) =>
                number > 1_000_000_000_000 ? Epoch(number) : DateTimeOffset.FromUnixTimeSeconds(number).ToLocalTime(),
            _ => null,
        };
    }

    private static DateTimeOffset? DateFromSeconds(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return DateTimeOffset.Now.AddSeconds(value.GetDouble());
    }

    private static DateTimeOffset? EpochDate(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return Epoch((long)value.GetDouble());
    }

    private static DateTimeOffset Epoch(long number) =>
        number > 1_000_000_000_000
            ? DateTimeOffset.FromUnixTimeMilliseconds(number).ToLocalTime()
            : DateTimeOffset.FromUnixTimeSeconds(number).ToLocalTime();

    private static DateTimeOffset? ParseDate(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
            ? value.ToLocalTime()
            : null;
}
