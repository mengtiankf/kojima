using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Kojima.Core.Config;
using Kojima.Core.Themes;
using Kojima.Island.Interop;
using Kojima.Island.Ipc;
using Kojima.Island.Island;
using Kojima.Island.Theme;
using Kojima.Island.Ui;

namespace Kojima.Island;

/// <summary>灵动岛 UI 进程：单实例、配置、主题、灵动岛窗口与功能子进程。</summary>
public partial class App : Application
{
    private const string MutexName = @"Local\Kojima.Island.SingleInstance";

    private Mutex? _mutex;
    private AppRuntime? _runtime;
    private IslandWindow? _island;
    private readonly List<IslandWindow> _extraIslands = [];
    private ShellBridge? _bridge;
    private HwndSource? _source;
    private bool _exiting;
    private string? _exitEvent;

    /// <summary>由 Shell 传入的命令管道名；为空表示独立运行（开发/调试）。</summary>
    public static string? ShellPipe { get; private set; }

    /// <summary>Shell 进程 pid，便携版更新脚本需要一并结束它。</summary>
    public static int ShellPid { get; private set; }

    /// <summary>Shell 可执行文件路径，更新与开机启动写这个路径。</summary>
    public static string? ShellPath { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ParseShellArguments(e.Args);

        AppLog.Init("island");
        HookCrashLogging();
        ThemeCatalog.Reload();

        _mutex = new Mutex(true, MutexName, out var created);
        if (!created)
        {
            AppDialog.Notify(
                null,
                "Kojima 已在运行",
                "已经有一个 Kojima 实例在运行，本次启动将退出。");
            Shutdown();
            return;
        }

        if (!string.IsNullOrWhiteSpace(ShellPath))
        {
            StartupRegistration.TargetPath = ShellPath;
        }

        _runtime = new AppRuntime(Dispatcher);
        AppRuntime.Current = _runtime;

        _island = new IslandWindow(_runtime);
        _island.Loaded += (_, _) => HookHotkeys();
        _island.Show();
        RebuildExtraIslands();
        if (_runtime.Config.Feature("weather").Enabled)
        {
            _runtime.Weather.Start();
        }

        _runtime.StartFeatures();

        StartShellBridge();
        ApplyDevHooks();
        AppLog.Info("Kojima 启动完成");
        _ = AutoCheckUpdatesAsync();
    }

    private static void ParseShellArguments(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--pipe" when i + 1 < args.Length:
                    ShellPipe = args[++i];
                    break;
                case "--parent" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out var pid))
                    {
                        ShellPid = pid;
                    }
                    break;
                case "--shell" when i + 1 < args.Length:
                    ShellPath = args[++i];
                    break;
            }
        }
    }

    private void StartShellBridge()
    {
        if (string.IsNullOrWhiteSpace(ShellPipe))
        {
            return;
        }

        _bridge = new ShellBridge(ShellPipe);
        _bridge.CommandReceived += OnShellCommand;
        _bridge.Start();
    }

    private void OnShellCommand(string command)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            switch (command)
            {
                case "toggle":
                    _island?.Toggle();
                    break;
                case "settings":
                    OpenSettings();
                    break;
                case "exit":
                    ExitApp();
                    break;
                case "restart":
                    RestartViaShell();
                    break;
            }
        }));
    }

    /// <summary>设置页“重启”：交给 Shell 重新拉起，独立运行时退回直接重启。</summary>
    public void RestartViaShell()
    {
        if (_bridge is { Connected: true })
        {
            _exitEvent = "restart";
            ExitApp();
            return;
        }

        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exe))
            {
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("重启失败：" + ex.Message);
        }

        ExitApp();
    }

    /// <summary>更新安装前先让 Shell 退出，避免 Kojima.exe 被占用，同时保留灵动岛下载收尾。</summary>
    public static void DetachShell()
    {
        if (Current is App app)
        {
            app._bridge?.Send("exit-detached");
        }
    }

    /// <summary>每天最多检查一次更新；失败只写日志，不打扰用户。</summary>
    private async Task AutoCheckUpdatesAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(12));
            var config = ConfigStore.Load();
            if (!config.Update.AutoCheck)
            {
                return;
            }

            var today = DateTime.Today.ToString("yyyy-MM-dd");
            if (config.Update.LastCheck == today)
            {
                return;
            }

            config.Update.LastCheck = today;
            ConfigStore.Save(config);
            await _runtime!.Updates.CheckAsync(manual: false);
        }
        catch (Exception ex)
        {
            AppLog.Warn("自动检查更新失败：" + ex.Message);
        }
    }

    /// <summary>把未处理异常写进 logs/shell.log，便于用户回报问题。</summary>
    private void HookCrashLogging()
    {
        DispatcherUnhandledException += (_, args) =>
            AppLog.Error("界面异常：" + args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLog.Error("未处理异常：" + args.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("后台任务异常：" + args.Exception.GetBaseException());
            args.SetObserved();
        };
    }

    /// <summary>开发/截图钩子：KOJIMA_PREVIEW 指定页面，KOJIMA_SHOT 输出窗口 PNG。</summary>
    private void ApplyDevHooks()
    {
        // KOJIMA_SETTINGS=1：启动后直接打开设置窗口（截图/调试用）
        if (Environment.GetEnvironmentVariable("KOJIMA_SETTINGS") is { Length: > 0 })
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                OpenSettings();
                var page = Environment.GetEnvironmentVariable("KOJIMA_SETTINGS_PAGE");
                if (int.TryParse(page, out var index) && _settings is { } window)
                {
                    window.SelectPage(index);
                }

                var clickProbe = Environment.GetEnvironmentVariable("KOJIMA_SETTINGS_SORTCLICK");
                if (!string.IsNullOrWhiteSpace(clickProbe) && _settings is { } clicker)
                {
                    var parts = clickProbe.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    var delta = parts.Length > 1 && parts[1].StartsWith("up", StringComparison.OrdinalIgnoreCase) ? -1 : 1;
                    var t1 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
                    t1.Tick += (_, _) =>
                    {
                        t1.Stop();
                        clicker.ClickOrder(parts[0], delta);
                        clicker.ClickOrder(parts[0], delta);
                    };
                    t1.Start();
                }

                var sortProbe = Environment.GetEnvironmentVariable("KOJIMA_SETTINGS_SORT");
                if (!string.IsNullOrWhiteSpace(sortProbe) && _settings is { } sorter)
                {
                    var parts = sortProbe.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    var delta = parts.Length > 1 && parts[1].StartsWith("up", StringComparison.OrdinalIgnoreCase) ? -1 : 1;
                    sorter.DebugMove(parts[0], delta);
                    sorter.DebugMove(parts[0], delta);
                }

                var module = Environment.GetEnvironmentVariable("KOJIMA_SETTINGS_MODULE");
                if (!string.IsNullOrWhiteSpace(module) && _settings is { } target)
                {
                    target.SelectModule(module);
                }

                var dropdown = Environment.GetEnvironmentVariable("KOJIMA_SETTINGS_DROPDOWN");
                if (!string.IsNullOrWhiteSpace(dropdown) && _settings is { } combo)
                {
                    var t2 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
                    t2.Tick += (_, _) =>
                    {
                        t2.Stop();
                        combo.DebugOpenCombo(dropdown);
                    };
                    t2.Start();
                }
            }));
        }

        if (Environment.GetEnvironmentVariable("KOJIMA_DEMO") == "1" && _island is not null)
        {
            _island.ApplyDemoData();
        }

        // KOJIMA_DIALOG=1：开发/截图钩子，展示新的主题对话框。
        if (Environment.GetEnvironmentVariable("KOJIMA_DIALOG") == "1")
        {
            var dialogTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
            dialogTimer.Tick += (_, _) =>
            {
                dialogTimer.Stop();
                AppDialog.Confirm(
                    null,
                    "需要重启",
                    "功能模块的开关会立即生效。\n要让所有界面与子进程完全按新配置运行，建议重启 Kojima。",
                    "立即重启",
                    "稍后");
            };
            dialogTimer.Start();
        }

        var preview = Environment.GetEnvironmentVariable("KOJIMA_PREVIEW");
        if (!string.IsNullOrWhiteSpace(preview) && _island is not null)
        {
            _island.ShowPage(preview);
            _island.Expand(true);
        }

        // KOJIMA_TOGGLE=<key>:<on|off>：不重启地切换功能并热重建导航（验证即时生效）
        var toggle = Environment.GetEnvironmentVariable("KOJIMA_TOGGLE");
        if (!string.IsNullOrWhiteSpace(toggle) && _runtime is not null && _island is not null)
        {
            var parts = toggle.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2)
            {
                var on = parts[1].Equals("on", StringComparison.OrdinalIgnoreCase);
                _runtime.Config.Feature(parts[0]).Enabled = on;
                _runtime.SaveConfig();
                _ = _runtime.SetFeatureAsync(parts[0], on).ContinueWith(_ =>
                {
                    Dispatcher.BeginInvoke(new Action(() => _island?.ReloadFeatures()));
                });
            }
        }

        // KOJIMA_TRANSLATE_UI=<文本>：把文本填进翻译页并触发一次真实翻译（用于 UI 验证）
        var probeUi = Environment.GetEnvironmentVariable("KOJIMA_TRANSLATE_UI");
        if (!string.IsNullOrWhiteSpace(probeUi))
        {
            var timerUi = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            timerUi.Tick += (_, _) =>
            {
                timerUi.Stop();
                _island?.TranslateProbe(probeUi);
            };
            timerUi.Start();
        }

        // KOJIMA_TRANSLATE=<文本>：命令行验证翻译通道（结果写入日志）
        var probe = Environment.GetEnvironmentVariable("KOJIMA_TRANSLATE");
        if (!string.IsNullOrWhiteSpace(probe) && _runtime is not null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var text = await _runtime.Translate.TranslateAsync(probe).ConfigureAwait(false);
                    AppLog.Info("翻译测试：" + text);
                }
                catch (Exception ex)
                {
                    AppLog.Warn("翻译测试失败：" + ex.Message);
                }
            });
        }

        // KOJIMA_MEDIA_PROBE=1：验证媒体会话与控制（暂停/播放切换后写日志）
        if (Environment.GetEnvironmentVariable("KOJIMA_MEDIA_PROBE") == "1" && _runtime is not null)
        {
            var mediaTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            mediaTimer.Tick += async (_, _) =>
            {
                mediaTimer.Stop();
                try
                {
                    var before = _runtime.Media.Current;
                    AppLog.Info($"媒体探测：{(before is null ? "无会话" : before.Title + " / " + before.Artist + " playing=" + before.IsPlaying)}");
                    if (before is not null)
                    {
                        await _runtime.Media.TogglePlayPauseAsync();
                        await Task.Delay(1600);
                        var after = _runtime.Media.Current;
                        AppLog.Info($"媒体探测：切换后 playing={after?.IsPlaying}");
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Warn("媒体探测失败：" + ex.Message);
                }
            };
            mediaTimer.Start();
        }

        var shot = Environment.GetEnvironmentVariable("KOJIMA_SHOT");
        if (string.IsNullOrWhiteSpace(shot) || _island is null)
        {
            return;
        }

        var delay = int.TryParse(Environment.GetEnvironmentVariable("KOJIMA_SHOT_DELAY"), out var parsed) ? parsed : 4000;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delay) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                _island.UpdateLayout();
                var w = Math.Max(1, (int)Math.Ceiling(_island.ActualWidth));
                var h = Math.Max(1, (int)Math.Ceiling(_island.ActualHeight));
                var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(_island);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(shot);
                encoder.Save(stream);
                AppLog.Info($"截图已保存：{shot} {w}x{h}");
            }
            catch (Exception ex)
            {
                AppLog.Warn("截图失败：" + ex.Message);
            }
        };
        timer.Start();
    }

    private void HookHotkeys()
    {
        if (_island is null)
        {
            return;
        }

        _source = HwndSource.FromHwnd(new WindowInteropHelper(_island).Handle);
        _source?.AddHook(WndProc);

        var handle = _source?.Handle ?? IntPtr.Zero;
        var keys = _runtime!.Config.Hotkeys;
        Register(handle, 1, keys.Toggle);
        Register(handle, 2, keys.Settings);
        Register(handle, 3, keys.Monitor);
    }

    private void Register(IntPtr handle, int id, string text)
    {
        if (handle == IntPtr.Zero || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var (modifiers, key) = ParseHotkey(text);
        if (key == 0)
        {
            return;
        }

        if (NativeMethods.RegisterHotKey(handle, id, modifiers | NativeMethods.MOD_NOREPEAT, key))
        {
            return;
        }

        // 被其它程序占用时退到备选组合，避免功能直接不可用
        if (id == 3)
        {
            var (altModifiers, altKey) = ParseHotkey("Ctrl+Alt+O");
            if (NativeMethods.RegisterHotKey(handle, id, altModifiers | NativeMethods.MOD_NOREPEAT, altKey))
            {
                AppLog.Info("热键 Ctrl+Alt+M 被占用，监控改用 Ctrl+Alt+O");
                return;
            }
        }

        AppLog.Warn($"热键注册失败：{text}");
    }

    internal static (uint Modifiers, uint Key) ParseHotkey(string text)
    {
        uint modifiers = 0;
        uint key = 0;
        foreach (var rawPart in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (rawPart.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    modifiers |= NativeMethods.MOD_CONTROL;
                    break;
                case "alt":
                    modifiers |= NativeMethods.MOD_ALT;
                    break;
                case "shift":
                    modifiers |= NativeMethods.MOD_SHIFT;
                    break;
                case "win":
                    modifiers |= 0x0008;
                    break;
                default:
                    if (rawPart.Length == 1 && char.IsLetterOrDigit(rawPart[0]))
                    {
                        key = char.ToUpperInvariant(rawPart[0]);
                    }
                    else if (rawPart.StartsWith('F') && int.TryParse(rawPart[1..], out var fn) && fn is >= 1 and <= 24)
                    {
                        key = (uint)(0x70 + fn - 1);
                    }

                    break;
            }
        }

        return (modifiers, key);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeMethods.WM_HOTKEY)
        {
            return IntPtr.Zero;
        }

        switch (wParam.ToInt32())
        {
            case 1:
                _island?.Toggle();
                handled = true;
                break;
            case 2:
                OpenSettings();
                handled = true;
                break;
            case 3:
                OpenMonitor();
                handled = true;
                break;
        }

        return IntPtr.Zero;
    }

    private Settings.SettingsWindow? _settings;

    /// <summary>所有入口（热键 / 灵岛齿轮 / 右键 / 首页按钮）统一走这里，保证只开一个设置窗口。</summary>
    public static void ShowSettingsDialog()
    {
        if (Application.Current is App app)
        {
            app.OpenSettings();
        }
    }

    /// <summary>设置里改了显示器 / 位置 / 尺寸后，让岛体立即重新布局。</summary>
    public static void RefreshIsland()
    {
        if (Application.Current is App app)
        {
            app._island?.RefreshAppearance();
            foreach (var extra in app._extraIslands)
            {
                extra.RefreshAppearance();
            }
        }
    }

    /// <summary>设置里改了“其他显示器也显示”后，重建副岛窗口。</summary>
    /// <summary>设置保存后，让岛上的股票页同步自选列表。</summary>
    public static void RefreshStocks()
    {
        if (Application.Current is App app)
        {
            app._island?.ReloadStocks();
            foreach (var extra in app._extraIslands)
            {
                extra.ReloadStocks();
            }
        }
    }

    public static void RebuildDecks()
    {
        if (Application.Current is App app)
        {
            app.RebuildExtraIslands();
        }
    }

    private void RebuildExtraIslands()
    {
        foreach (var window in _extraIslands.ToList())
        {
            try
            {
                window.Close();
            }
            catch (Exception ex)
            {
                AppLog.Warn("关闭副岛失败：" + ex.Message);
            }
        }

        _extraIslands.Clear();
        if (_runtime is null)
        {
            return;
        }

        var primary = DisplayInfo.Find(_runtime.Config.Island.Monitor);
        foreach (var device in _runtime.Config.Island.ExtraMonitors.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var display = DisplayInfo.Find(device);
            if (display is null || string.Equals(display.DeviceName, primary?.DeviceName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var extra = new IslandWindow(_runtime, display.DeviceName);
                extra.Show();
                _extraIslands.Add(extra);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"创建副岛失败（{device}）：{ex.Message}");
            }
        }
    }

    private void OpenSettings()
    {
        if (_runtime is null)
        {
            return;
        }

        // 兜底：即使引用丢了，只要进程里已有可见的设置窗口就复用它
        var visible = Application.Current?.Windows
            .OfType<Settings.SettingsWindow>()
            .FirstOrDefault(w => w.IsVisible);
        if (visible is not null)
        {
            _settings = visible;
        }

        if (_settings is { IsLoaded: true } existing && existing.IsVisible)
        {
            if (existing.WindowState == WindowState.Minimized)
            {
                existing.WindowState = WindowState.Normal;
            }

            existing.Show();
            existing.Activate();
            existing.Topmost = true;
            existing.Topmost = false;
            existing.Focus();
            return;
        }

        var window = new Settings.SettingsWindow(_runtime);
        window.Saved += Reapply;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_settings, window))
            {
                _settings = null;
            }
        };
        _settings = window;
        window.Show();
        window.Activate();
    }

    private void OpenMonitor()
    {
        _island?.ShowPage("monitor");
    }

    private void Reapply()
    {
        if (_runtime is null)
        {
            return;
        }

        _runtime.Themes.Apply();
        _island?.RefreshAppearance();
        foreach (var extra in _extraIslands)
        {
            extra.RefreshAppearance();
        }
    }

    private void ExitApp()
    {
        _exiting = true;
        _bridge?.Send(_exitEvent ?? "exit");
        _exitEvent = null;
        _island?.Close();

        try
        {
            _runtime?.DisposeAsync().AsTask().Wait(3000);
        }
        catch (Exception ex)
        {
            AppLog.Warn("退出清理失败：" + ex.Message);
        }

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!_exiting)
        {
            try
            {
                _runtime?.DisposeAsync().AsTask().Wait(2000);
            }
            catch (Exception ex)
            {
                AppLog.Warn("退出清理失败：" + ex.Message);
            }
        }

        _bridge?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
