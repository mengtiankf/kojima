using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kojima.Core.Config;
using Kojima.Core.Ipc;
using Kojima.Core.Models;
using Microsoft.Data.Sqlite;

namespace Kojima.Agents.Features;

/// <summary>
/// 消息中心：只读轮询 Windows 通知数据库（wpndatabase.db），把 QQ / 微信 / 邮件等 toast 映射成本地消息列表。
/// 隐私：只保留发送者/首行文字（最多 40 字），不保存正文，不写回任何系统位置。
/// </summary>
public sealed class MessageFeature : IAgentFeature
{
    private static readonly string DbPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Microsoft", "Windows", "Notifications", "wpndatabase.db");

    private static readonly Regex TextTag = new(@"<text\b[^>]*>(.*?)</text>", RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly (string Prefix, string Name, string Kind)[] ToastApps =
    [
        ("QQ", "QQ", "qq"),
        ("Weixin", "微信", "wechat"),
        ("WeChat", "微信", "wechat"),
        ("ThunderbirdToast", "邮件", "mail"),
    ];

    private readonly List<MessageItem> _items = [];
    private readonly Dictionary<string, DateTime> _lastByKind = [];
    private AgentPipeServer? _server;
    private Timer? _timer;
    private long _maxId = -1;
    private int _busy;
    private volatile bool _running;
    private int _pollMs = 1500;

    public bool Busy => _running;

    public void Attach(AgentPipeServer server) => _server = server;

    public ValueTask DisposeAsync()
    {
        Stop();
        return ValueTask.CompletedTask;
    }

    public Task<object?> HandleAsync(string method, JsonElement? args)
    {
        switch (method.ToLowerInvariant())
        {
            case "messages.start":
                Start();
                return Task.FromResult<object?>(Items());
            case "messages.list":
                return Task.FromResult<object?>(Items());
            case "messages.restart":
                Stop();
                Start();
                return Task.FromResult<object?>(new { ok = true });
            case "messages.stop":
                Stop();
                return Task.FromResult<object?>(new { ok = true });
            case "messages.clear":
                _items.Clear();
                Push();
                return Task.FromResult<object?>(new { ok = true });
            case "messages.seed":
                Seed(args);
                return Task.FromResult<object?>(Items());
            default:
                throw new InvalidOperationException("未知方法：" + method);
        }
    }

    private void Start()
    {
        if (_running)
        {
            return;
        }

        try
        {
            _pollMs = Math.Clamp(ConfigStore.Load().Messages.PollMs, 500, 10000);
        }
        catch
        {
            _pollMs = 1500;
        }

        _running = true;
        _timer = new Timer(_ => Poll(), null, 0, _pollMs);
    }

    private void Stop()
    {
        _running = false;
        _timer?.Dispose();
        _timer = null;
    }

    private void Poll()
    {
        if (!_running || Interlocked.Exchange(ref _busy, 1) == 1)
        {
            return;
        }

        var since = _maxId;
        Task.Run(() => ReadNew(since)).ContinueWith(task =>
        {
            Interlocked.Exchange(ref _busy, 0);
            if (task.IsFaulted)
            {
                Core.Config.AppLog.Warn("读取通知失败：" + task.Exception?.GetBaseException().Message);
                return;
            }

            if (task.Result.Count > 0)
            {
                Absorb(task.Result);
            }
        }, TaskScheduler.Default);
    }

    private void Absorb(List<(long Id, MessageItem Item)> rows)
    {
        foreach (var (id, item) in rows)
        {
            _maxId = Math.Max(_maxId, id);
            var last = _lastByKind.GetValueOrDefault(item.Kind, DateTime.MinValue);
            if ((item.At - last).TotalSeconds < 8)
            {
                continue;
            }

            _lastByKind[item.Kind] = item.At;
            _items.Insert(0, item);
        }

        while (_items.Count > 50)
        {
            _items.RemoveAt(_items.Count - 1);
        }

        Push();
    }

    private List<MessageItem> Items() => _items.ToList();

    private void Push() => _ = _server?.PushAsync("messages", Items());

    private void Seed(JsonElement? args)
    {
        if (args is { ValueKind: JsonValueKind.Object } payload &&
            payload.TryGetProperty("items", out var items) &&
            items.ValueKind == JsonValueKind.Array)
        {
            try
            {
                var seeded = items.Deserialize<List<MessageItem>>(IpcJson.Options);
                if (seeded is { Count: > 0 })
                {
                    _items.InsertRange(0, seeded);
                    Push();
                }
            }
            catch
            {
                // 忽略演示数据错误
            }
        }
    }

    private List<(long, MessageItem)> ReadNew(long since)
    {
        var rows = new List<(long, MessageItem)>();
        if (!File.Exists(DbPath))
        {
            return rows;
        }

        var temp = "";
        try
        {
            var csb = new SqliteConnectionStringBuilder
            {
                DataSource = DbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
            };
            using var connection = new SqliteConnection(csb.ToString());
            connection.Open();

            if (since < 0)
            {
                using var history = connection.CreateCommand();
                history.CommandText =
                    "SELECT n.Id, h.PrimaryId, n.ArrivalTime, n.Type, n.Payload " +
                    "FROM Notification n JOIN NotificationHandler h ON h.RecordId = n.HandlerId " +
                    "ORDER BY n.Id DESC LIMIT 20";
                using var reader = history.ExecuteReader();
                var raw = 0;
                while (reader.Read())
                {
                    raw++;
                    if (MapRow(reader) is { } mapped)
                    {
                        rows.Add(mapped);
                    }
                }

                Core.Config.AppLog.Info($"通知原始行：{raw} 条");
                rows.Reverse();
                Core.Config.AppLog.Info($"通知历史：{rows.Count} 条（since<0）");
                return rows;
            }

            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                "SELECT n.Id, h.PrimaryId, n.ArrivalTime, n.Type, n.Payload " +
                "FROM Notification n JOIN NotificationHandler h ON h.RecordId = n.HandlerId " +
                "WHERE n.Id > $id ORDER BY n.Id LIMIT 40";
            cmd.Parameters.AddWithValue("$id", since);
            using var reader2 = cmd.ExecuteReader();
            while (reader2.Read())
            {
                if (MapRow(reader2) is { } mapped)
                {
                    rows.Add(mapped);
                }
            }
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            try
            {
                temp = Path.Combine(Path.GetTempPath(), "kojima-wpn-" + Guid.NewGuid().ToString("N") + ".db");
                File.Copy(DbPath, temp, overwrite: true);
                rows.AddRange(ReadNewFromCopy(temp, since));
            }
            catch (Exception inner)
            {
                Core.Config.AppLog.Warn("读取通知数据库失败：" + inner.Message);
            }
        }
        finally
        {
            if (temp.Length > 0)
            {
                try { File.Delete(temp); } catch { /* 忽略 */ }
            }
        }

        return rows;
    }

    private static List<(long, MessageItem)> ReadNewFromCopy(string path, long since)
    {
        var rows = new List<(long, MessageItem)>();
        var csb = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        using var connection = new SqliteConnection(csb.ToString());
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT n.Id, h.PrimaryId, n.ArrivalTime, n.Type, n.Payload " +
            "FROM Notification n JOIN NotificationHandler h ON h.RecordId = n.HandlerId " +
            "WHERE n.Id > $id ORDER BY n.Id LIMIT 40";
        cmd.Parameters.AddWithValue("$id", since);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (MapRow(reader) is { } mapped)
            {
                rows.Add(mapped);
            }
        }

        return rows;
    }

    private static (long Id, MessageItem Item)? MapRow(SqliteDataReader reader)
    {
        var id = reader.GetInt64(0);
        var primary = reader.IsDBNull(1) ? "" : reader.GetString(1);
        var arrival = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
        var type = reader.IsDBNull(3) ? "" : reader.GetString(3);
        var payload = reader.IsDBNull(4) ? Array.Empty<byte>() : (byte[])reader.GetValue(4);
        var app = MapApp(primary);
        if (app is null || (!string.IsNullOrEmpty(type) && !type.Equals("toast", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var sender = app.Value.Kind is "qq" or "wechat" ? FirstText(payload) : "";
        return (id, new MessageItem(app.Value.Name, app.Value.Kind, sender, FileTimeToLocal(arrival), "toast"));
    }

    private static (string Name, string Kind)? MapApp(string primary)
    {
        foreach (var (prefix, name, kind) in ToastApps)
        {
            if (!primary.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            if (primary.Length == prefix.Length || !char.IsLetterOrDigit(primary[prefix.Length]))
            {
                return (name, kind);
            }
        }

        var fallback = FriendlyName(primary);
        return fallback.Length == 0 ? null : (fallback, "other");
    }

    private static string FriendlyName(string primary)
    {
        if (string.IsNullOrWhiteSpace(primary))
        {
            return "";
        }

        var name = primary;
        var bang = name.IndexOf('!');
        if (bang > 0)
        {
            name = name[..bang];
        }

        var underscore = name.IndexOf("_8weky", StringComparison.OrdinalIgnoreCase);
        if (underscore > 0)
        {
            name = name[..underscore];
        }

        if (name.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase))
        {
            name = "微软 " + name["Microsoft.".Length..];
        }

        if (name.StartsWith("electron.app.", StringComparison.OrdinalIgnoreCase))
        {
            name = name["electron.app.".Length..];
        }
        else if (name.StartsWith("electron.", StringComparison.OrdinalIgnoreCase))
        {
            name = name["electron.".Length..];
        }
        else if (name.StartsWith("com.", StringComparison.OrdinalIgnoreCase)
                 || name.StartsWith("org.", StringComparison.OrdinalIgnoreCase)
                 || name.StartsWith("io.", StringComparison.OrdinalIgnoreCase))
        {
            var parts = name.Split('.');
            if (parts.Length >= 3)
            {
                name = parts[^1];
            }
        }

        var exe = name.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0)
        {
            name = name[..exe];
        }

        name = name.Replace("pcsute", "手机助手").Trim();

        if (name.Length == 0 || name.Any(c => char.IsWhiteSpace(c) || c > 0x7F))
        {
            return "应用通知";
        }

        return name.Length > 18 ? name[..18] : name;
    }

    private static string FirstText(byte[] payload)
    {
        try
        {
            var text = Encoding.UTF8.GetString(payload);
            var match = TextTag.Match(text);
            if (!match.Success)
            {
                return "";
            }

            var value = Regex.Replace(match.Groups[1].Value, "<[^>]+>", "").Trim();
            value = value.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"")
                .Replace("&apos;", "'").Replace("&amp;", "&").Trim();
            return value.Length > 40 ? value[..40] : value;
        }
        catch
        {
            return "";
        }
    }

    private static DateTime FileTimeToLocal(long value)
    {
        try
        {
            return value <= 0 ? DateTime.Now : DateTime.FromFileTime(value).ToLocalTime();
        }
        catch
        {
            return DateTime.Now;
        }
    }
}
