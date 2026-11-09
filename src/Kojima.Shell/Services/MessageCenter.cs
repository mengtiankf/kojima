using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using Kojima.Core.Config;
using Microsoft.Data.Sqlite;

namespace Kojima.Shell.Services;

/// <summary>一条 QQ / 微信 / 邮件等系统通知（只保留发送者或首行文字，不读正文）。</summary>
public sealed record MessageItem(string App, string Kind, string Sender, DateTime At, string Source)
{
    public string TimeText => At.ToString("HH:mm");
    public string DayText => At.Date == DateTime.Today ? "今天" : At.ToString("M月d日");
}

/// <summary>
/// 消息中心：只读轮询 Windows 通知数据库（wpndatabase.db），把 QQ / 微信 / 邮件等 toast 映射成本地消息列表。
/// 移植自 wisland 的 notify.py（toast 数据库 + PrimaryId 前缀映射 + 首行 &lt;text&gt; 作为发送者）。
/// 隐私：只保留发送者/首行文字（最多 40 字），不保存正文，不写回任何系统位置。
/// </summary>
public sealed class MessageCenter : IDisposable
{
    private static readonly string DbPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Microsoft", "Windows", "Notifications", "wpndatabase.db");

    private static readonly Regex TextTag = new(@"<text\b[^>]*>(.*?)</text>", RegexOptions.Singleline | RegexOptions.IgnoreCase);

    // PrimaryId 前缀 → (显示名, 类型)
    private static readonly (string Prefix, string Name, string Kind)[] ToastApps =
    [
        ("QQ", "QQ", "qq"),
        ("Weixin", "微信", "wechat"),
        ("WeChat", "微信", "wechat"),
        ("ThunderbirdToast", "邮件", "mail"),
    ];

    private readonly Dispatcher _dispatcher;
    private readonly KojimaConfig _config;
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background);
    private readonly List<MessageItem> _items = [];
    private readonly Dictionary<string, DateTime> _lastByKind = [];
    private long _maxId = -1;
    private int _busy;
    private bool _running;

    public MessageCenter(Dispatcher dispatcher, KojimaConfig config)
    {
        _dispatcher = dispatcher;
        _config = config;
    }

    public IReadOnlyList<MessageItem> Items => _items;

    public IReadOnlyDictionary<string, int> Counts { get; private set; } = new Dictionary<string, int>();

    public event Action? Changed;

    public void Start()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(_config.Messages.PollMs, 500, 10000));
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
        Poll();
    }

    public void Stop()
    {
        _running = false;
        _timer.Stop();
    }

    public void Clear()
    {
        _items.Clear();
        Counts = new Dictionary<string, int>();
        Changed?.Invoke();
    }

    /// <summary>打开消息来源应用：优先用配置里的路径，其次按进程名尝试。</summary>
    public void Open(string kind)
    {
        var path = _config.Messages.Open.GetValueOrDefault(kind, "");
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return;
            }

            var name = kind switch { "qq" => "QQ", "wechat" => "Weixin", "mail" => "thunderbird", _ => kind };
            var running = Process.GetProcessesByName(name).FirstOrDefault();
            if (running is not null)
            {
                ActivateWindow(running.Id);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("打开消息应用失败：" + ex.Message);
        }
    }

    /// <summary>截图 / 演示用：塞入几条假消息。</summary>
    public void Seed(IEnumerable<MessageItem> items)
    {
        _items.InsertRange(0, items);
        Recount();
        Changed?.Invoke();
    }

    private void Poll()
    {
        if (!_running || Interlocked.Exchange(ref _busy, 1) == 1)
        {
            return;
        }

        var since = _maxId;
        Task.Run(() => ReadNew(since)).ContinueWith(t =>
        {
            Interlocked.Exchange(ref _busy, 0);
            if (t.IsFaulted)
            {
                AppLog.Warn("读取通知失败：" + t.Exception?.GetBaseException().Message);
                return;
            }

            if (t.Result.Count == 0)
            {
                return;
            }

            _dispatcher.BeginInvoke(() => Absorb(t.Result));
        });
    }

    private void Absorb(List<(long Id, MessageItem Item)> rows)
    {
        foreach (var (id, item) in rows)
        {
            _maxId = Math.Max(_maxId, id);
            var last = _lastByKind.GetValueOrDefault(item.Kind, DateTime.MinValue);
            if ((item.At - last).TotalSeconds < 8)
            {
                continue; // 同一应用 8 秒内只记一条，避免一次弹窗刷屏
            }

            _lastByKind[item.Kind] = item.At;
            _items.Insert(0, item);
        }

        while (_items.Count > 50)
        {
            _items.RemoveAt(_items.Count - 1);
        }

        Recount();
        Changed?.Invoke();
    }

    private void Recount()
    {
        Counts = _items
            .GroupBy(i => i.Kind)
            .ToDictionary(g => g.Key, g => g.Count());
    }

    private List<(long, MessageItem)> ReadNew(long since)
    {
        var rows = new List<(long, MessageItem)>();
        if (!File.Exists(DbPath))
        {
            return rows;
        }

        var source = DbPath;
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
                // 首次启动：把最近 20 条已映射通知带进来，页面不至于一片空白
                using var history = connection.CreateCommand();
                history.CommandText =
                    "SELECT n.Id, h.PrimaryId, n.ArrivalTime, n.Type, n.Payload " +
                    "FROM Notification n JOIN NotificationHandler h ON h.RecordId = n.HandlerId " +
                    "ORDER BY n.Id DESC LIMIT 20";
                using var hr = history.ExecuteReader();
                var raw = 0;
                while (hr.Read())
                {
                    raw++;
                    var mapped = MapRow(hr);
                    if (mapped is { } m)
                    {
                        rows.Add(m);
                    }
                }

                AppLog.Info($"通知原始行：{raw} 条");

                rows.Reverse();
                AppLog.Info($"通知历史：{rows.Count} 条（since<0）");
                return rows;
            }

            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                "SELECT n.Id, h.PrimaryId, n.ArrivalTime, n.Type, n.Payload " +
                "FROM Notification n JOIN NotificationHandler h ON h.RecordId = n.HandlerId " +
                "WHERE n.Id > $id ORDER BY n.Id LIMIT 40";
            cmd.Parameters.AddWithValue("$id", since);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                var primary = reader.IsDBNull(1) ? "" : reader.GetString(1);
                var arrival = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
                var type = reader.IsDBNull(3) ? "" : reader.GetString(3);
                var payload = reader.IsDBNull(4) ? Array.Empty<byte>() : (byte[])reader.GetValue(4);

                var app = MapApp(primary);
                if (app is null)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(type) && !type.Equals("toast", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var sender = app.Value.Kind is "qq" or "wechat" ? FirstText(payload) : "";
                rows.Add((id, new MessageItem(app.Value.Name, app.Value.Kind, sender, FileTimeToLocal(arrival), "toast")));
            }
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            // 数据库被 Windows 独占时拷贝一份再读
            try
            {
                temp = Path.Combine(Path.GetTempPath(), "kojima-wpn-" + Guid.NewGuid().ToString("N") + ".db");
                File.Copy(DbPath, temp, overwrite: true);
                rows.AddRange(ReadNewFromCopy(temp, since));
            }
            catch (Exception inner)
            {
                AppLog.Warn("读取通知数据库失败：" + inner.Message);
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

    private List<(long, MessageItem)> ReadNewFromCopy(string path, long since)
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
            var id = reader.GetInt64(0);
            var primary = reader.IsDBNull(1) ? "" : reader.GetString(1);
            var arrival = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
            var type = reader.IsDBNull(3) ? "" : reader.GetString(3);
            var payload = reader.IsDBNull(4) ? Array.Empty<byte>() : (byte[])reader.GetValue(4);
            var app = MapApp(primary);
            if (app is null || (!string.IsNullOrEmpty(type) && !type.Equals("toast", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var sender = app.Value.Kind is "qq" or "wechat" ? FirstText(payload) : "";
            rows.Add((id, new MessageItem(app.Value.Name, app.Value.Kind, sender, FileTimeToLocal(arrival), "toast")));
        }

        return rows;
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

        // 常见打包器的前缀：electron.app.xxx / com.vendor.xxx
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

        // 有些应用把通知正文塞进 PrimaryId：含空格或中文就当作未知应用，避免把正文当应用名
        if (name.Length == 0 || name.Any(c => char.IsWhiteSpace(c) || c > 0x7F))
        {
            return "应用通知";
        }

        return name.Length > 18 ? name[..18] : name;
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

            // 前缀后必须是结尾或非字母数字，避免 QQExtension 命中 QQ
            if (primary.Length == prefix.Length || !char.IsLetterOrDigit(primary[prefix.Length]))
            {
                return (name, kind);
            }
        }

        // 兜底：其它应用的通知也收，显示名从 PrimaryId 前缀推导（去掉 AUMID 包后缀）
        var fallback = FriendlyName(primary);
        return fallback.Length == 0 ? null : (fallback, "other");
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

    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int cmd);

    private static void ActivateWindow(int pid)
    {
        var handle = Process.GetProcessById(pid).MainWindowHandle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        ShowWindow(handle, SW_RESTORE);
        SetForegroundWindow(handle);
    }

    public void Dispose()
    {
        Stop();
        _items.Clear();
    }
}
