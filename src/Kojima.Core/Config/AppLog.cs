using System.Text;

namespace Kojima.Core.Config;

/// <summary>极简日志：同时写 %LOCALAPPDATA%\Kojima\logs\shell.log。子进程各自写自己的文件。</summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static string? _path;

    public static void Init(string name)
    {
        ConfigStore.EnsureDirectories();
        _path = Path.Combine(ConfigStore.LogsDirectory, name + ".log");
        TryTrim(_path);
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
        lock (Gate)
        {
            try
            {
                if (_path is not null)
                {
                    File.AppendAllText(_path, line, Encoding.UTF8);
                }
            }
            catch
            {
                // 日志失败不能影响主流程
            }
        }
    }

    private static void TryTrim(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length > 2 * 1024 * 1024)
            {
                File.Move(path, path + ".old", overwrite: true);
            }
        }
        catch
        {
            // 忽略
        }
    }
}
