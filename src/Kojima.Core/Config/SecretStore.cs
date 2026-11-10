using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Kojima.Core.Config;

/// <summary>
/// 密钥库：%LOCALAPPDATA%\Kojima\secrets.dat。
/// 内容 = "KJ1:" + base64(DPAPI(CurrentUser, 附加熵) 加密后的 JSON 字典)。
/// 只有当前 Windows 用户能解密；不写日志、不写 config.json。
/// </summary>
[SupportedOSPlatform("windows")]
public static class SecretStore
{
    private const string Header = "KJ1:";

    // 附加熵把密文绑定到 Kojima，避免同用户其它程序的密文被直接挪用。
    private static readonly byte[] Entropy =
        SHA256.HashData(Encoding.UTF8.GetBytes("Kojima.SecretStore.v1"));

    private static readonly object Gate = new();
    private static Dictionary<string, string>? _values;
    private static DateTime _stamp;
    private static long _length = -1;

    public static string FilePath => Path.Combine(ConfigStore.RootDirectory, "secrets.dat");

    /// <summary>读取密钥；不存在返回空串。</summary>
    public static string Get(string name)
    {
        lock (Gate)
        {
            EnsureLoadedLocked();
            return _values!.TryGetValue(name, out var value) ? value : "";
        }
    }

    public static bool Has(string name) => Get(name).Length > 0;

    /// <summary>返回快照，调用方可以安全遍历。</summary>
    public static IReadOnlyDictionary<string, string> Snapshot()
    {
        lock (Gate)
        {
            EnsureLoadedLocked();
            return new Dictionary<string, string>(_values!, StringComparer.Ordinal);
        }
    }

    /// <summary>写入或删除（空串 = 删除）；有变化才落盘。</summary>
    public static void Set(string name, string? value) =>
        SetMany([new KeyValuePair<string, string?>(name, value)]);

    /// <summary>批量写入，一次落盘。</summary>
    public static void SetMany(IEnumerable<KeyValuePair<string, string?>> values)
    {
        lock (Gate)
        {
            EnsureLoadedLocked();
            var changed = false;
            foreach (var pair in values)
            {
                if (string.IsNullOrWhiteSpace(pair.Key))
                {
                    continue;
                }

                var name = pair.Key.Trim();
                var value = pair.Value ?? "";
                if (value.Length == 0)
                {
                    changed |= _values!.Remove(name);
                }
                else if (!_values!.TryGetValue(name, out var old) || !string.Equals(old, value, StringComparison.Ordinal))
                {
                    _values[name] = value;
                    changed = true;
                }
            }

            if (changed)
            {
                WriteLocked();
            }
        }
    }

    public static void Remove(string name) => Set(name, null);

    /// <summary>
    /// 从旧版配置（明文密钥）导入。已有非空密钥时保留现有值，避免覆盖更新。
    /// 返回实际导入的条数。
    /// </summary>
    public static int ImportLegacy(IEnumerable<KeyValuePair<string, string>> values)
    {
        lock (Gate)
        {
            EnsureLoadedLocked();
            var imported = 0;
            foreach (var pair in values)
            {
                if (string.IsNullOrWhiteSpace(pair.Key))
                {
                    continue;
                }

                var name = pair.Key.Trim();
                var value = (pair.Value ?? "").Trim();
                if (value.Length == 0)
                {
                    continue;
                }

                if (_values!.TryGetValue(name, out var existing) && existing.Length > 0)
                {
                    continue;
                }

                _values[name] = value;
                imported++;
            }

            if (imported > 0)
            {
                WriteLocked();
                AppLog.Info($"secrets: 已从旧配置导入 {imported} 条密钥并加密保存");
            }

            return imported;
        }
    }

    /// <summary>路径或文件变化后强制下次读取时重载。</summary>
    public static void Invalidate()
    {
        lock (Gate)
        {
            _values = null;
            _stamp = default;
            _length = -1;
        }
    }

    private static void EnsureLoadedLocked()
    {
        var info = new FileInfo(FilePath);
        var exists = info.Exists;
        var stamp = exists ? info.LastWriteTimeUtc : default;
        var length = exists ? info.Length : -1;

        if (_values is not null && stamp == _stamp && length == _length)
        {
            return;
        }

        _values = ReadLocked(out var plaintextLegacy);
        _stamp = stamp;
        _length = length;

        if (plaintextLegacy)
        {
            // 兼容早期实验版：发现明文 JSON 就立即改写为 DPAPI 密文。
            AppLog.Warn("secrets.dat 是明文旧格式，正在重新加密");
            WriteLocked();
        }
    }

    private static Dictionary<string, string> ReadLocked(out bool plaintextLegacy)
    {
        plaintextLegacy = false;
        try
        {
            if (!File.Exists(FilePath))
            {
                return NewMap();
            }

            var text = File.ReadAllText(FilePath).Trim();
            if (text.Length == 0)
            {
                return NewMap();
            }

            if (text.StartsWith(Header, StringComparison.Ordinal))
            {
                var cipher = Convert.FromBase64String(text[Header.Length..]);
                var plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
                try
                {
                    return Deserialize(plain) ?? NewMap();
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plain);
                }
            }

            var map = Deserialize(Encoding.UTF8.GetBytes(text));
            if (map is not null)
            {
                plaintextLegacy = true;
                return map;
            }

            throw new InvalidDataException("secrets.dat 不是可识别的格式");
        }
        catch (Exception ex)
        {
            AppLog.Warn("secrets.dat 读取失败：" + ex.Message);
            TryBackupBroken();
            return NewMap();
        }
    }

    private static void WriteLocked()
    {
        try
        {
            Directory.CreateDirectory(ConfigStore.RootDirectory);
            var json = JsonSerializer.SerializeToUtf8Bytes(_values ?? NewMap());
            byte[] cipher;
            try
            {
                cipher = ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(json);
            }

            var payload = Header + Convert.ToBase64String(cipher);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, payload);
            File.Move(tmp, FilePath, overwrite: true);

            var info = new FileInfo(FilePath);
            _stamp = info.LastWriteTimeUtc;
            _length = info.Length;
        }
        catch (Exception ex)
        {
            AppLog.Warn("secrets.dat 写入失败：" + ex.Message);
        }
    }

    private static Dictionary<string, string>? Deserialize(byte[] json)
    {
        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        if (map is null)
        {
            return null;
        }

        var normalized = NewMap();
        foreach (var pair in map)
        {
            if (!string.IsNullOrWhiteSpace(pair.Key))
            {
                normalized[pair.Key.Trim()] = pair.Value ?? "";
            }
        }

        return normalized;
    }

    private static Dictionary<string, string> NewMap() => new(StringComparer.Ordinal);

    private static void TryBackupBroken()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var backup = Path.Combine(
                    ConfigStore.RootDirectory, $"secrets.broken-{DateTime.Now:yyyyMMdd-HHmmss}.dat");
                File.Copy(FilePath, backup, overwrite: true);
            }
        }
        catch
        {
            // 备份失败不影响启动；密钥丢失时用户可以重新填写。
        }
    }
}
