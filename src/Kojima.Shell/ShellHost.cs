using System.Diagnostics;
using System.Drawing;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using Kojima.Core.Config;

namespace Kojima.Shell;

/// <summary>托盘 + 单实例 + 灵动岛子进程的监督器；本身不创建任何 WPF 窗口。</summary>
internal sealed class ShellHost : ApplicationContext
{
    private const string PipeName = "kojima-shell";

    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _watch;
    private readonly object _writeLock = new();
    private readonly string _shellExe;
    private bool _openSettings;
    private NamedPipeServerStream? _server;
    private StreamWriter? _writer;
    private Process? _island;
    private DateTime _restartDue = DateTime.MinValue;
    private DateTime _lastStart = DateTime.MinValue;
    private int _crashes;
    private bool _exiting;
    private bool _restarting;
    private bool _detached;
    private bool _pendingExit;

    public ShellHost(string[] args)
    {
        _shellExe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Kojima.exe");
        _openSettings = args.Any(a => a.Equals("--settings", StringComparison.OrdinalIgnoreCase));

        var menu = new ContextMenuStrip();
        menu.Items.Add("打开面板", null, (_, _) => Send("toggle"));
        menu.Items.Add("设置", null, (_, _) => Send("settings"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) =>
        {
            _detached = false;
            _pendingExit = true;
        });

        _tray = new NotifyIcon
        {
            Text = "Kojima",
            Icon = LoadIcon(),
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                Send("toggle");
            }
        };
        _tray.DoubleClick += (_, _) => Send("settings");

        _watch = new System.Windows.Forms.Timer { Interval = 400 };
        _watch.Tick += (_, _) => Watch();
        _watch.Start();

        StartPipeServer();
        LaunchIsland();
    }

    public void Start() => AppLog.Info("Kojima Shell 已启动，等待灵动岛子进程");

    /// <summary>第二个实例：让正在运行的实例打开设置页，然后退出。</summary>
    public static void NotifyExisting()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                pipe.Connect(500);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
                writer.WriteLine(JsonSerializer.Serialize(new { cmd = "settings" }));
                return;
            }
            catch
            {
                Thread.Sleep(300);
            }
        }

        MessageBox.Show("Kojima 已经在运行。", "Kojima", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void StartPipeServer() => _ = Task.Run(AcceptLoopAsync);

    private async Task AcceptLoopAsync()
    {
        while (!_exiting)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync().ConfigureAwait(false);
                var writer = new StreamWriter(server, new UTF8Encoding(false)) { AutoFlush = true };
                lock (_writeLock)
                {
                    _server = server;
                    _writer = writer;
                }

                AppLog.Info("灵动岛子进程已连接");
                using var reader = new StreamReader(server, Encoding.UTF8);
                string? line;
                while (!_exiting && (line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
                {
                    OnIslandLine(line);
                }
            }
            catch (Exception ex)
            {
                if (!_exiting)
                {
                    AppLog.Warn("灵动岛通信中断：" + ex.Message);
                }
            }
            finally
            {
                lock (_writeLock)
                {
                    _writer = null;
                    _server = null;
                }

                server?.Dispose();
            }

            if (!_exiting)
            {
                await Task.Delay(500).ConfigureAwait(false);
            }
        }
    }

    private void OnIslandLine(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            if (!document.RootElement.TryGetProperty("event", out var value) ||
                value.GetString() is not { } name)
            {
                return;
            }

            switch (name)
            {
                case "ready":
                    AppLog.Info("灵动岛已就绪");
                    if (_openSettings)
                    {
                        _openSettings = false;
                        Send("settings");
                    }
                    break;
                case "restart":
                    _restarting = true;
                    AppLog.Info("灵动岛请求重启");
                    break;
                case "exit":
                    _detached = true;
                    _pendingExit = true;
                    break;
                case "exit-detached":
                    // 更新安装前只退出 Shell，保留灵动岛继续启动安装程序。
                    _detached = true;
                    _pendingExit = true;
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("解析灵动岛事件失败：" + ex.Message);
        }
    }

    private void Watch()
    {
        if (_pendingExit)
        {
            _pendingExit = false;
            Shutdown(_detached);
            return;
        }

        if (_exiting)
        {
            return;
        }

        if (_island is { HasExited: true } island)
        {
            var code = 0;
            try
            {
                code = island.ExitCode;
            }
            catch
            {
                // 进程可能已被回收
            }

            island.Dispose();
            _island = null;

            if (_restarting || DateTime.Now - _lastStart > TimeSpan.FromMinutes(3))
            {
                _crashes = 0;
            }
            else
            {
                _crashes++;
            }

            _restarting = false;
            if (_crashes > 5)
            {
                _tray.ShowBalloonTip(3000, "Kojima", "灵动岛连续启动失败，已停止重试。", ToolTipIcon.Error);
                Shutdown(detach: false);
                return;
            }

            AppLog.Warn($"灵动岛子进程已退出（code={code}），准备重启");
            _restartDue = DateTime.Now.AddSeconds(1);
        }

        if (_island is null && _restartDue != DateTime.MinValue && DateTime.Now >= _restartDue)
        {
            LaunchIsland();
        }
    }

    private void LaunchIsland()
    {
        var exe = ResolveIslandPath();
        if (exe is null)
        {
            MessageBox.Show(
                "找不到 Kojima.Island.exe，请重新安装 Kojima。",
                "Kojima",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            Shutdown(detach: false);
            return;
        }

        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        info.ArgumentList.Add("--pipe");
        info.ArgumentList.Add(PipeName);
        info.ArgumentList.Add("--parent");
        info.ArgumentList.Add(Environment.ProcessId.ToString());
        info.ArgumentList.Add("--shell");
        info.ArgumentList.Add(_shellExe);

        try
        {
            _island = Process.Start(info) ??
                      throw new InvalidOperationException("Process.Start 返回 null");
            _lastStart = DateTime.Now;
            _restartDue = DateTime.MinValue;
            AppLog.Info("已启动灵动岛子进程 pid=" + _island.Id);
        }
        catch (Exception ex)
        {
            AppLog.Error("启动灵动岛失败：" + ex);
            _restartDue = DateTime.Now.AddSeconds(5);
        }
    }

    private static string? ResolveIslandPath()
    {
        var env = Environment.GetEnvironmentVariable("KOJIMA_ISLAND_EXE");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            return env;
        }

        var baseDir = AppContext.BaseDirectory;
        var candidates = new List<string> { Path.Combine(baseDir, "Kojima.Island.exe") };

        // 开发环境：dotnet run 时两个项目各自在自己的 bin 目录。
        var sourceRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));
        foreach (var configuration in new[] { "Release", "Debug" })
        {
            candidates.Add(Path.Combine(
                sourceRoot,
                "Kojima.Island",
                "bin",
                configuration,
                "net10.0-windows10.0.19041.0",
                "Kojima.Island.exe"));
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private bool Send(string cmd)
    {
        lock (_writeLock)
        {
            if (_writer is null)
            {
                return false;
            }

            try
            {
                _writer.WriteLine(JsonSerializer.Serialize(new { cmd }));
                _writer.Flush();
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Warn("向灵动岛发送命令失败：" + ex.Message);
                return false;
            }
        }
    }

    private void Shutdown(bool detach)
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        try
        {
            _watch.Stop();
        }
        catch
        {
            // 忽略
        }

        if (!detach)
        {
            Send("exit");
            try
            {
                if (_island is { HasExited: false })
                {
                    _island.WaitForExit(2000);
                }
            }
            catch
            {
                // 忽略
            }

            if (_island is { HasExited: false })
            {
                try
                {
                    _island.Kill(entireProcessTree: true);
                }
                catch
                {
                    // 忽略
                }
            }
        }

        _tray.Visible = false;
        _tray.Dispose();
        try
        {
            _server?.Dispose();
        }
        catch
        {
            // 忽略
        }

        ExitThread();
    }

    private static Icon LoadIcon()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "assets", "kojima.ico");
            if (File.Exists(path))
            {
                return new Icon(path);
            }

            var process = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(process))
            {
                var extracted = Icon.ExtractAssociatedIcon(process);
                if (extracted is not null)
                {
                    return extracted;
                }
            }
        }
        catch
        {
            // 忽略
        }

        return SystemIcons.Application;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _watch.Dispose();
            _tray.Dispose();
            _server?.Dispose();
        }

        base.Dispose(disposing);
    }
}
