using System.Net.Http;
using System.Text.Json;
using Kojima.Core.Ipc;
using Kojima.Core.Updates;

namespace Kojima.Agents.Features;

/// <summary>更新检查与安装包下载放在子进程；主进程只负责选择包、退出并拉起安装器。</summary>
public sealed class UpdatesFeature : IAgentFeature
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    private AgentPipeServer? _server;

    public bool Busy => false;

    public void Attach(AgentPipeServer server)
    {
        _server = server;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public async Task<object?> HandleAsync(string method, JsonElement? args)
    {
        var payload = args is { ValueKind: JsonValueKind.Object } value ? value : default;
        switch (method.ToLowerInvariant())
        {
            case "updates.check":
                return await CheckAsync(payload).ConfigureAwait(false);
            case "updates.download":
                return await DownloadAsync(payload).ConfigureAwait(false);
            default:
                throw new InvalidOperationException("未知方法：" + method);
        }
    }

    private static async Task<UpdateRelease?> CheckAsync(JsonElement args)
    {
        var text = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("currentVersion", out var value)
            ? value.GetString() ?? ""
            : "";
        if (!UpdateService.TryParseVersion(text, out var current))
        {
            throw new InvalidOperationException("无法识别的当前版本：" + text);
        }

        return await UpdateService.CheckAsync(current, Http).ConfigureAwait(false);
    }

    private async Task<object> DownloadAsync(JsonElement args)
    {
        var url = String(args, "url");
        var name = String(args, "name");
        var path = String(args, "path");
        if (url.Length == 0 || name.Length == 0 || path.Length == 0)
        {
            return new { ok = false, error = "缺少下载参数" };
        }

        var size = args.TryGetProperty("size", out var sizeValue) && sizeValue.TryGetInt64(out var bytes) ? bytes : 0;
        var asset = new UpdateAsset(name, url, size);
        var lastReport = 0L;
        var progress = new Progress<long>(read =>
        {
            var now = Environment.TickCount64;
            if (now - lastReport < 100)
            {
                return;
            }

            lastReport = now;
            _ = _server?.PushAsync("updates.progress", new { read, total = size });
        });
        await UpdateService.DownloadAsync(asset, path, bytes: progress).ConfigureAwait(false);
        _ = _server?.PushAsync("updates.progress", new { read = size, total = size });

        var shaUrl = String(args, "shaUrl");
        if (shaUrl.Length == 0)
        {
            return new { ok = true };
        }

        var expected = await ReadExpectedAsync(shaUrl).ConfigureAwait(false);
        if (expected.Length == 0)
        {
            return new { ok = true };
        }

        var actual = UpdateService.Sha256File(path);
        if (actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            return new { ok = true };
        }

        try
        {
            File.Delete(path);
        }
        catch
        {
            // 删除失败不改变校验结论
        }

        return new { ok = false, error = "SHA-256 校验失败" };
    }

    private static async Task<string> ReadExpectedAsync(string url)
    {
        var text = await Http.GetStringAsync(url).ConfigureAwait(false);
        var token = text.Trim().Split(' ', '\t', '\r', '\n')[0];
        return token.Length == 64 ? token : "";
    }

    private static string String(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value)
            ? value.GetString() ?? ""
            : "";
}
