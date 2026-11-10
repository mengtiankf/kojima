using System.Globalization;
using Kojima.Core.Config;
using Kojima.Core.Models;
using Microsoft.Data.Sqlite;

namespace Kojima.Agents.Usage;

/// <summary>
/// Antigravity 2.x / Antigravity CLI 的会话数据库读取器。
/// 每个会话一个 SQLite 文件；steps.metadata 里的 field 9 是 model_usage，
/// gen_metadata.data 里的 field 1 是 ChatModelMetadata，用最小 protobuf 解析器读取。
/// </summary>
internal static class AntigravityUsage
{
    public static UsageSnapshot Read(DateOnly start, DateOnly end)
    {
        var snapshot = new UsageSnapshot
        {
            Pid = "antigravity",
            Name = "Antigravity",
            Source = "本机",
            Scope = "日志",
            Status = "ok",
        };

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var folders = new[]
        {
            Path.Combine(home, ".gemini", "antigravity", "conversations"),
            Path.Combine(home, ".gemini", "antigravity-cli", "conversations"),
        };
        var existing = folders.Where(Directory.Exists).ToList();
        if (existing.Count == 0)
        {
            snapshot.Note = "未找到本机 Antigravity 会话记录。";
            return snapshot;
        }

        var startDate = start.ToDateTime(TimeOnly.MinValue);
        var endDate = end.ToDateTime(TimeOnly.MaxValue);
        var entries = new Dictionary<string, TokenEntry>(StringComparer.Ordinal);
        var sessions = 0;
        var failed = 0;

        foreach (var database in existing.SelectMany(SafeFiles))
        {
            try
            {
                var info = new FileInfo(database);
                var wal = new FileInfo(database + "-wal");
                var modified = wal.Exists && wal.LastWriteTimeUtc > info.LastWriteTimeUtc
                    ? wal.LastWriteTimeUtc
                    : info.LastWriteTimeUtc;
                if (modified < startDate) continue;

                sessions++;
                foreach (var entry in Parse(database, startDate, endDate)) entries.TryAdd(entry.Key, entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or InvalidOperationException)
            {
                failed++;
                AppLog.Warn($"Antigravity 会话读取失败：{Path.GetFileName(database)}：{ex.Message}");
            }
        }

        snapshot.Entries = entries.Values.OrderBy(e => e.Time).ToList();
        snapshot.Note = sessions == 0
            ? "未找到本机 Antigravity 会话记录。"
            : $"本机 Antigravity 与 Antigravity CLI {start:yyyy-MM-dd} 起 · {sessions} 个会话";
        if (failed > 0) snapshot.Note += $" · {failed} 个会话暂时无法读取";
        snapshot.Complete = failed == 0;
        return snapshot;
    }

    private static IEnumerable<string> SafeFiles(string folder)
    {
        try { return Directory.EnumerateFiles(folder, "*.db").ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static List<TokenEntry> Parse(string database, DateTime start, DateTime end)
    {
        var entries = new List<TokenEntry>();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();

        var models = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT hex(data) FROM gen_metadata WHERE data IS NOT NULL";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var chat = Field(Bytes(reader.GetString(0)), 1);
                var messageId = Text(Field(Field(chat, 4), 7));
                var model = Text(Field(chat, 19));
                if (messageId.Length > 0 && model.Length > 0) models[messageId] = model;
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT hex(metadata) FROM steps WHERE metadata IS NOT NULL";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var metadata = Bytes(reader.GetString(0));
                var usage = Field(metadata, 9);
                var seconds = Varint(metadata, 1);
                if (usage.Length == 0 || seconds is not { } unix || unix > long.MaxValue) continue;

                var messageId = Text(Field(usage, 7));
                if (messageId.Length == 0) continue;

                DateTimeOffset time;
                try { time = DateTimeOffset.FromUnixTimeSeconds((long)unix).ToLocalTime(); }
                catch (ArgumentOutOfRangeException) { continue; }
                if (time.DateTime < start || time.DateTime > end) continue;

                var input = Count(usage, 2);
                var cacheRead = Count(usage, 5);
                var cacheWrite = Count(usage, 4);
                var output = Count(usage, 3);
                if (input + cacheRead + cacheWrite + output <= 0) continue;

                entries.Add(new TokenEntry
                {
                    Key = "ag:" + messageId,
                    Time = time,
                    Model = models.GetValueOrDefault(messageId, ""),
                    Input = input,
                    CacheRead = cacheRead,
                    CacheWrite = cacheWrite,
                    Output = output,
                });
            }
        }

        return entries;
    }

    private static byte[] Bytes(string? hex)
    {
        try { return string.IsNullOrEmpty(hex) ? [] : Convert.FromHexString(hex); }
        catch (FormatException) { return []; }
    }

    private static long Count(byte[] message, int field) =>
        (long)Math.Min(Varint(message, field) ?? 0, long.MaxValue);

    private static string Text(byte[] data) => System.Text.Encoding.UTF8.GetString(data);

    private static byte[] Field(byte[] message, int field)
    {
        foreach (var (number, wire, _, data) in Fields(message))
            if (number == field && wire == 2) return data;
        return [];
    }

    private static ulong? Varint(byte[] message, int field)
    {
        foreach (var (number, wire, value, _) in Fields(message))
            if (number == field && wire == 0) return value;
        return null;
    }

    private static IEnumerable<(int Number, int Wire, ulong Value, byte[] Data)> Fields(byte[] message)
    {
        var i = 0;
        while (i < message.Length)
        {
            if (!TryVarint(message, ref i, out var key)) yield break;
            var number = (int)(key >> 3);
            var wire = (int)(key & 7);
            if (wire == 0)
            {
                if (!TryVarint(message, ref i, out var value)) yield break;
                yield return (number, wire, value, []);
            }
            else if (wire == 2)
            {
                if (!TryVarint(message, ref i, out var length) || length > (ulong)(message.Length - i)) yield break;
                var data = new byte[(int)length];
                Buffer.BlockCopy(message, i, data, 0, (int)length);
                i += (int)length;
                yield return (number, wire, 0, data);
            }
            else if (wire == 5)
            {
                if (i + 4 > message.Length) yield break;
                i += 4;
            }
            else if (wire == 1)
            {
                if (i + 8 > message.Length) yield break;
                i += 8;
            }
            else yield break;
        }
    }

    private static bool TryVarint(byte[] data, ref int index, out ulong value)
    {
        value = 0;
        var shift = 0;
        while (index < data.Length && shift < 64)
        {
            var b = data[index++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
            shift += 7;
        }
        return false;
    }
}
