using System.Globalization;
using System.Text.Json;
using Kojima.Core.Models;
using Microsoft.Data.Sqlite;

namespace Kojima.Agents.Usage;

/// <summary>
/// 本机日志读取的扩展部分：Kimi Code、OpenCode、ZCode。
/// 三家都把用量写在本机文件里，不依赖任何在线账号；读取失败时保留说明而不是静默变成 unknown。
/// </summary>
public static class LocalUsageExtra
{
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    // ---------------------------------------------------------------- Kimi

    public static UsageSnapshot ReadKimi(DateOnly start, DateOnly end)
    {
        var snapshot = new UsageSnapshot
        {
            Pid = "kimi",
            Name = "Kimi",
            Source = "本机",
            Scope = "日志",
        };

        try
        {
            var home = Environment.GetEnvironmentVariable("KIMI_CODE_HOME");
            if (string.IsNullOrWhiteSpace(home))
            {
                home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".kimi-code");
            }

            var sessions = Path.Combine(home, "sessions");
            if (!Directory.Exists(sessions))
            {
                return Empty(snapshot, "未发现 Kimi Code 本机会话");
            }

            var files = Directory
                .EnumerateFiles(sessions, "wire.jsonl", SearchOption.AllDirectories)
                .Where(path => path.Contains(Path.DirectorySeparatorChar + "agents" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                .Take(4000)
                .ToArray();

            if (files.Length == 0)
            {
                return Empty(snapshot, "未发现 Kimi Code 本机会话");
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in files)
            {
                ReadKimiFile(file, start, end, seen, snapshot.Entries);
            }

            snapshot.Entries.Sort((a, b) => a.Time.CompareTo(b.Time));
            snapshot.UsageStart = snapshot.Entries.Count > 0 ? snapshot.Entries[0].Time : null;
            snapshot.Note = $"{files.Length} 个会话 · {snapshot.Entries.Count} 次模型调用";
            snapshot.Status = "ok";
            snapshot.Complete = true;
        }
        catch (Exception ex)
        {
            snapshot.Status = "error";
            snapshot.Error = ex.Message;
            snapshot.Complete = false;
        }

        return snapshot;
    }

    private static void ReadKimiFile(string path, DateOnly start, DateOnly end, HashSet<string> seen, List<TokenEntry> entries)
    {
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (!line.Contains("usage.record", StringComparison.Ordinal))
                {
                    continue;
                }

                using var doc = JsonDocument.Parse(line, JsonOptions);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("type", out var type) ||
                    type.GetString() != "usage.record" ||
                    !root.TryGetProperty("usage", out var usage) ||
                    usage.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var model = Text(root, "model");
                if (model.StartsWith("kimi-code/", StringComparison.OrdinalIgnoreCase))
                {
                    model = model["kimi-code/".Length..];
                }

                var time = Number(root, "time") is { } millis
                    ? FromMilliseconds((long)millis)
                    : new DateTimeOffset(File.GetLastWriteTime(path));

                if (!InRange(time, start, end))
                {
                    continue;
                }

                var agent = Text(root, "agentId");
                var input = (long)(Number(usage, "inputOther") ?? Number(usage, "input") ?? 0);
                var output = (long)(Number(usage, "output") ?? 0);
                var read = (long)(Number(usage, "inputCacheRead") ?? 0);
                var write = (long)(Number(usage, "inputCacheCreation") ?? 0);
                var key = string.Create(
                    CultureInfo.InvariantCulture,
                    $"kimi:{agent}:{time.ToUnixTimeMilliseconds()}:{model}:{input}:{read}:{write}:{output}");

                if (!seen.Add(key))
                {
                    continue;
                }

                entries.Add(new TokenEntry
                {
                    Key = key,
                    Time = time,
                    Model = model,
                    Input = Math.Max(0, input),
                    CacheRead = Math.Max(0, read),
                    CacheWrite = Math.Max(0, write),
                    Output = Math.Max(0, output),
                });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }
    }

    // ------------------------------------------------------- OpenCode 1.x / 2.x

    public static UsageSnapshot ReadOpenCode(DateOnly start, DateOnly end)
    {
        var snapshot = new UsageSnapshot
        {
            Pid = "opencode",
            Name = "OpenCode",
            Source = "本机",
            Scope = "数据库",
        };

        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            var baseFolder = !string.IsNullOrWhiteSpace(xdg) && Path.IsPathFullyQualified(xdg)
                ? xdg
                : Path.Combine(home, ".local", "share");
            var data = Path.Combine(baseFolder, "opencode");
            var custom = Environment.GetEnvironmentVariable("OPENCODE_DB");
            var database = string.IsNullOrWhiteSpace(custom) || custom == ":memory:"
                ? Path.Combine(data, "opencode.db")
                : Path.GetFullPath(Path.Combine(data, custom));

            if (!File.Exists(database))
            {
                return Empty(snapshot, "未发现 OpenCode 数据库");
            }

            using var connection = Open(database);
            var seen = new HashSet<string>(StringComparer.Ordinal);

            if (HasTable(connection, "session_message"))
            {
                ReadOpenCodeRows(
                    connection,
                    "SELECT id, time_created, data FROM session_message WHERE type = 'assistant'",
                    start,
                    end,
                    seen,
                    snapshot.Entries,
                    v2: true);
            }

            if (HasTable(connection, "message"))
            {
                ReadOpenCodeRows(
                    connection,
                    "SELECT id, time_created, data FROM message",
                    start,
                    end,
                    seen,
                    snapshot.Entries,
                    v2: false);
            }

            snapshot.Entries.Sort((a, b) => a.Time.CompareTo(b.Time));
            snapshot.UsageStart = snapshot.Entries.Count > 0 ? snapshot.Entries[0].Time : null;
            snapshot.Note = $"{snapshot.Entries.Count} 次模型调用（1.x 与 2.x 表自动合并）";
            snapshot.Status = "ok";
            snapshot.Complete = true;
        }
        catch (Exception ex)
        {
            snapshot.Status = "error";
            snapshot.Error = ex.Message;
            snapshot.Complete = false;
        }

        return snapshot;
    }

    private static void ReadOpenCodeRows(
        SqliteConnection connection,
        string sql,
        DateOnly start,
        DateOnly end,
        HashSet<string> seen,
        List<TokenEntry> entries,
        bool v2)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.IsDBNull(0) ? "" : reader.GetString(0);
                var millis = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                var time = FromMilliseconds(millis);
                if (!InRange(time, start, end))
                {
                    continue;
                }

                var json = reader.IsDBNull(2) ? null : reader.GetString(2);
                if (string.IsNullOrEmpty(json))
                {
                    continue;
                }

                using var doc = JsonDocument.Parse(json, JsonOptions);
                var data = doc.RootElement;
                if (!v2 && data.TryGetProperty("role", out var role) && role.GetString() != "assistant")
                {
                    continue;
                }

                if (!data.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var model = v2
                    ? data.TryGetProperty("model", out var modelNode) ? Text(modelNode, "id") : ""
                    : Text(data, "modelID");

                var input = (long)(Number(tokens, "input") ?? 0);
                var output = (long)(Number(tokens, "output") ?? 0);
                var read = tokens.TryGetProperty("cache", out var cache) ? (long)(Number(cache, "read") ?? 0) : 0;
                var write = tokens.TryGetProperty("cache", out var cache2) ? (long)(Number(cache2, "write") ?? 0) : 0;
                var cost = Number(data, "cost");
                var key = string.Create(CultureInfo.InvariantCulture, $"opencode:{id}:{input}:{read}:{write}:{output}");

                if (!seen.Add(key))
                {
                    continue;
                }

                entries.Add(new TokenEntry
                {
                    Key = key,
                    Time = time,
                    Model = model,
                    Input = Math.Max(0, input),
                    CacheRead = Math.Max(0, read),
                    CacheWrite = Math.Max(0, write),
                    Output = Math.Max(0, output),
                    Cost = cost is > 0 ? cost : null,
                });
            }
        }
        catch (SqliteException)
        {
            // 旧版本可能没有 time_created / data 列；忽略这一张表。
        }
        catch (JsonException)
        {
        }
    }

    // ---------------------------------------------------------------- ZCode

    public static UsageSnapshot ReadZCode(DateOnly start, DateOnly end)
    {
        var snapshot = new UsageSnapshot
        {
            Pid = "zcode",
            Name = "ZCode",
            Source = "本机",
            Scope = "数据库",
        };

        try
        {
            var home = Environment.GetEnvironmentVariable("ZCODE_HOME");
            if (string.IsNullOrWhiteSpace(home))
            {
                home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".zcode");
            }

            var database = Path.Combine(home, "cli", "db", "db.sqlite");
            if (!File.Exists(database))
            {
                return Empty(snapshot, "未发现 ZCode 数据库");
            }

            using var connection = Open(database);
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, model_id, COALESCE(completed_at, started_at),
                       input_tokens, output_tokens, cache_read_input_tokens, cache_creation_input_tokens
                FROM model_usage
                WHERE status <> 'running'
                """;

            using var reader = command.ExecuteReader();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read())
            {
                var id = reader.IsDBNull(0) ? "" : reader.GetString(0);
                var model = reader.IsDBNull(1) ? "" : reader.GetString(1);
                var millis = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
                var time = FromMilliseconds(millis);
                if (!InRange(time, start, end))
                {
                    continue;
                }

                var rawInput = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);
                var output = reader.IsDBNull(4) ? 0 : reader.GetInt64(4);
                var read = reader.IsDBNull(5) ? 0 : reader.GetInt64(5);
                var write = reader.IsDBNull(6) ? 0 : reader.GetInt64(6);
                var input = Math.Max(0, rawInput - read - write);
                var key = $"zcode:{id}";

                if (!seen.Add(key))
                {
                    continue;
                }

                snapshot.Entries.Add(new TokenEntry
                {
                    Key = key,
                    Time = time,
                    Model = model,
                    Input = input,
                    CacheRead = read,
                    CacheWrite = write,
                    Output = output,
                });
            }

            snapshot.Entries.Sort((a, b) => a.Time.CompareTo(b.Time));
            snapshot.UsageStart = snapshot.Entries.Count > 0 ? snapshot.Entries[0].Time : null;
            snapshot.Note = $"{snapshot.Entries.Count} 次模型调用";
            snapshot.Status = "ok";
            snapshot.Complete = true;
        }
        catch (Exception ex)
        {
            snapshot.Status = "error";
            snapshot.Error = ex.Message;
            snapshot.Complete = false;
        }

        return snapshot;
    }

    // ---------------------------------------------------------------- helpers

    internal static UsageSnapshot Empty(UsageSnapshot snapshot, string note)
    {
        snapshot.Status = "ok";
        snapshot.Note = note;
        snapshot.Complete = true;
        return snapshot;
    }

    internal static bool InRange(DateTimeOffset time, DateOnly start, DateOnly end)
    {
        var day = DateOnly.FromDateTime(time.LocalDateTime);
        return day >= start && day <= end;
    }

    internal static DateTimeOffset FromMilliseconds(long millis)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(millis).ToLocalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTimeOffset.Now;
        }
    }

    internal static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    internal static double? Number(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    internal static SqliteConnection Open(string database)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return connection;
    }

    private static bool HasTable(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1";
        command.Parameters.AddWithValue("$name", table);
        return command.ExecuteScalar() is not null;
    }
}
