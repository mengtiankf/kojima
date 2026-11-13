using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Kojima.Core.Config;
using Kojima.Core.Models;

namespace Kojima.Agents.Usage;

/// <summary>本机会话日志读取：dsh / Claude Code / Codex。</summary>
public static class LocalUsage
{
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public static UsageSnapshot Read(string source, DateOnly start, DateOnly end)
    {
        var snapshot = source.ToLowerInvariant() switch
        {
            "claude" => ReadClaude(start, end),
            "codex" => ReadCodex(start, end),
            "dsh" => ReadDsh(start, end),
            _ => new UsageSnapshot { Pid = source, Name = source, Status = "unknown" },
        };
        snapshot.Pid = source;
        return snapshot;
    }

    // ---------------------------------------------------------------- dsh

    private static UsageSnapshot ReadDsh(DateOnly start, DateOnly end)
    {
        var snapshot = new UsageSnapshot
        {
            Pid = "dsh",
            Name = "DeepSeek Harness",
            Source = "本机",
            Scope = "日志",
        };

        try
        {
            var root = Environment.GetEnvironmentVariable("DSH_HOME");
            if (string.IsNullOrWhiteSpace(root))
            {
                root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
            }

            var sessions = Path.Combine(root, "sessions");
            if (!Directory.Exists(sessions))
            {
                snapshot.Status = "ok";
                snapshot.Note = "未发现本机会话";
                return snapshot;
            }

            var files = CollectSessionFiles(sessions);
            if (files.Count == 0)
            {
                snapshot.Status = "ok";
                snapshot.Note = "未发现本机会话";
                return snapshot;
            }

            var first = DateTimeOffset.MaxValue;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in files)
            {
                foreach (var entry in ReadDshFile(file, seen))
                {
                    if (entry.Time < first)
                    {
                        first = entry.Time;
                    }

                    snapshot.Entries.Add(entry);
                }
            }

            snapshot.UsageStart = first == DateTime.MaxValue ? null : first;
            snapshot.Note = snapshot.UsageStart is { } s
                ? $"本地 {s:yyyy-MM-dd} 起 · {files.Count} 个会话 · {snapshot.Entries.Count} 次模型请求"
                : $"{files.Count} 个会话";
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

    private static List<FileInfo> CollectSessionFiles(string sessions)
    {
        var cutoff = DateTime.UtcNow.AddDays(-35);
        var list = new List<FileInfo>();
        try
        {
            foreach (var project in Directory.EnumerateDirectories(sessions))
            {
                foreach (var session in Directory.EnumerateDirectories(project))
                {
                    // 每个会话目录只取最高版本
                    FileInfo? best = null;
                    foreach (var candidate in Directory.EnumerateFiles(session, "session.v*.jsonl*"))
                    {
                        var info = new FileInfo(candidate);
                        if (info.LastWriteTimeUtc < cutoff)
                        {
                            continue;
                        }

                        if (best is null || string.CompareOrdinal(info.Name, best.Name) > 0)
                        {
                            best = info;
                        }
                    }

                    if (best is not null)
                    {
                        list.Add(best);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("扫描 dsh 会话失败：" + ex.Message);
        }

        list.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
        return list.Count > 400 ? list.GetRange(0, 400) : list;
    }

    private static IEnumerable<TokenEntry> ReadDshFile(FileInfo file, HashSet<string> seen)
    {
        using var stream = OpenMaybeCompressed(file.FullName);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length < 20 || !line.Contains("assistant/message", StringComparison.Ordinal))
            {
                continue;
            }

            TokenEntry? entry = null;
            try
            {
                using var document = JsonDocument.Parse(line, JsonOptions);
                var root = document.RootElement;
                if (!root.TryGetProperty("type", out var type) || type.GetString() != "assistant/message")
                {
                    continue;
                }

                if (!root.TryGetProperty("data", out var data) || !data.TryGetProperty("usage", out var usage)
                    || usage.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var message = data.TryGetProperty("message", out var m) ? m : default;
                var id = message.ValueKind == JsonValueKind.Object && message.TryGetProperty("id", out var mid)
                    ? mid.GetString()
                    : null;
                var key = id is { Length: > 0 } ? "dsh:" + id : $"dsh:{file.Name}:{root.GetProperty("seq").GetInt64()}";
                if (!seen.Add(key))
                {
                    continue;
                }

                var time = DateTimeOffset.FromUnixTimeMilliseconds(root.GetProperty("time").GetInt64()).LocalDateTime;
                var model = ReadModel(message);
                entry = new TokenEntry
                {
                    Key = key,
                    Time = time,
                    Model = model,
                    Input = ReadLong(usage, "inputTokens"),
                    CacheRead = ReadLong(usage, "cacheReadTokens"),
                    CacheWrite = ReadLong(usage, "cacheWriteTokens"),
                    Output = ReadLong(usage, "outputTokens"),
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
            }

            if (entry is not null && entry.Total > 0)
            {
                yield return entry;
            }
        }
    }

    private static string ReadModel(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("source", out var source))
        {
            return "";
        }

        if (source.ValueKind == JsonValueKind.Object && source.TryGetProperty("model", out var model))
        {
            return model.GetString() ?? "";
        }

        return "";
    }

    // ---------------------------------------------------------------- claude

    private static UsageSnapshot ReadClaude(DateOnly start, DateOnly end)
    {
        var snapshot = new UsageSnapshot { Pid = "claude", Name = "Claude", Source = "本机", Scope = "日志" };
        try
        {
            var home = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            if (string.IsNullOrWhiteSpace(home))
            {
                home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
            }

            var projects = Path.Combine(home, "projects");
            if (!Directory.Exists(projects))
            {
                snapshot.Status = "ok";
                snapshot.Note = "未发现本机会话（没有 Claude Code 记录目录）";
                return snapshot;
            }

            var cutoff = DateTime.UtcNow.AddDays(-35);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in Directory.EnumerateFiles(projects, "*.jsonl", SearchOption.AllDirectories))
            {
                var info = new FileInfo(file);
                if (info.LastWriteTimeUtc < cutoff)
                {
                    continue;
                }

                foreach (var entry in ReadClaudeFile(info, seen))
                {
                    snapshot.Entries.Add(entry);
                }
            }

            snapshot.Status = "ok";
            snapshot.Complete = true;
            snapshot.Note = snapshot.Entries.Count == 0
                ? "未发现本机会话"
                : $"{snapshot.Entries.Count} 次模型请求";
        }
        catch (Exception ex)
        {
            snapshot.Status = "error";
            snapshot.Error = ex.Message;
        }

        return snapshot;
    }

    private static IEnumerable<TokenEntry> ReadClaudeFile(FileInfo file, HashSet<string> seen)
    {
        foreach (var line in File.ReadLines(file.FullName))
        {
            if (!line.Contains("\"usage\"", StringComparison.Ordinal))
            {
                continue;
            }

            TokenEntry? entry = null;
            try
            {
                using var document = JsonDocument.Parse(line, JsonOptions);
                var root = document.RootElement;
                if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var id = message.TryGetProperty("id", out var mid) ? mid.GetString() : null;
                var stamp = root.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.String
                    ? DateTimeOffset.TryParse(ts.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed)
                        ? parsed.LocalDateTime
                        : file.LastWriteTime
                    : file.LastWriteTime;
                var key = id is { Length: > 0 } ? "claude:" + id : $"claude:{file.Name}:{stamp.Ticks}";
                if (!seen.Add(key))
                {
                    continue;
                }

                entry = new TokenEntry
                {
                    Key = key,
                    Time = stamp,
                    Model = message.TryGetProperty("model", out var model) ? model.GetString() ?? "" : "",
                    Input = ReadLong(usage, "input_tokens"),
                    CacheRead = ReadLong(usage, "cache_read_input_tokens"),
                    CacheWrite = ReadLong(usage, "cache_creation_input_tokens"),
                    Output = ReadLong(usage, "output_tokens"),
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
            }

            if (entry is not null && entry.Total > 0)
            {
                yield return entry;
            }
        }
    }

    // ---------------------------------------------------------------- codex

    private static UsageSnapshot ReadCodex(DateOnly start, DateOnly end)
    {
        var snapshot = new UsageSnapshot { Pid = "codex", Name = "Codex", Source = "本机", Scope = "日志" };
        try
        {
            var home = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (string.IsNullOrWhiteSpace(home))
            {
                home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            }

            if (!Directory.Exists(home))
            {
                snapshot.Status = "ok";
                snapshot.Note = "未发现本机会话（没有 Codex 记录目录）";
                return snapshot;
            }

            var cutoff = DateTime.UtcNow.AddDays(-35);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var folder in new[] { "sessions", "archived_sessions" })
            {
                var path = Path.Combine(home, folder);
                if (!Directory.Exists(path))
                {
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(path, "*.jsonl", SearchOption.AllDirectories))
                {
                    var info = new FileInfo(file);
                    if (info.LastWriteTimeUtc < cutoff)
                    {
                        continue;
                    }

                    foreach (var entry in ReadCodexFile(info, seen))
                    {
                        snapshot.Entries.Add(entry);
                    }
                }
            }

            snapshot.Status = "ok";
            snapshot.Complete = true;
            snapshot.Note = snapshot.Entries.Count == 0 ? "未发现本机会话" : $"{snapshot.Entries.Count} 次模型请求";
        }
        catch (Exception ex)
        {
            snapshot.Status = "error";
            snapshot.Error = ex.Message;
        }

        return snapshot;
    }

    private static IEnumerable<TokenEntry> ReadCodexFile(FileInfo file, HashSet<string> seen)
    {
        var model = "";
        foreach (var line in File.ReadLines(file.FullName))
        {
            if (!line.Contains("token_count", StringComparison.Ordinal) && !line.Contains("turn_context", StringComparison.Ordinal))
            {
                continue;
            }

            TokenEntry? entry = null;
            try
            {
                using var document = JsonDocument.Parse(line, JsonOptions);
                var root = document.RootElement;
                var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type == "turn_context")
                {
                    var payload = root.TryGetProperty("payload", out var p) ? p : default;
                    if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("model", out var m))
                    {
                        model = m.GetString() ?? model;
                    }

                    continue;
                }

                if (type != "event_msg")
                {
                    continue;
                }

                var body = root.TryGetProperty("payload", out var payload2) ? payload2 : default;
                if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("type", out var bt) || bt.GetString() != "token_count")
                {
                    continue;
                }

                if (!body.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var usage = info.TryGetProperty("last_token_usage", out var last) && last.ValueKind == JsonValueKind.Object
                    ? last
                    : info.TryGetProperty("total_token_usage", out var total) ? total : default;
                if (usage.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var stamp = root.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.String
                    ? DateTimeOffset.TryParse(ts.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                        ? parsed.LocalDateTime
                        : file.LastWriteTime
                    : file.LastWriteTime;
                var input = ReadLong(usage, "input_tokens");
                var cached = ReadLong(usage, "cached_input_tokens");
                var key = $"codex:{file.Name}:{usage.GetRawText().GetHashCode():X8}";
                if (!seen.Add(key))
                {
                    continue;
                }

                entry = new TokenEntry
                {
                    Key = key,
                    Time = stamp,
                    Model = model,
                    Input = Math.Max(0, input - cached),
                    CacheRead = cached,
                    Output = ReadLong(usage, "output_tokens"),
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
            }

            if (entry is not null && entry.Total > 0)
            {
                yield return entry;
            }
        }
    }

    // ---------------------------------------------------------------- helpers

    private static Stream OpenMaybeCompressed(string path)
    {
        var stream = File.OpenRead(path);
        if (!path.EndsWith(".zstd", StringComparison.OrdinalIgnoreCase)
            && !path.EndsWith(".zst", StringComparison.OrdinalIgnoreCase))
        {
            return stream;
        }

        using (stream)
        {
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            var data = buffer.ToArray();
            using var decompressor = new ZstdSharp.Decompressor();
            var plain = decompressor.Unwrap(data).ToArray();
            return new MemoryStream(plain);
        }
    }

    private static long ReadLong(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;
    }
}
