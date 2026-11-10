using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Kojima.Core.Config;
using Kojima.Core.Themes;
using Kojima.Shell.Interop;
using Kojima.Shell.Island;
using Kojima.Shell.Theme;

namespace Kojima.Shell;

/// <summary>主进程：单实例、配置、主题、灵动岛、托盘与热键。</summary>
public partial class App : Application
{
    private const string MutexName = @"Local\Kojima.Shell.SingleInstance";

    private Mutex? _mutex;
    private AppRuntime? _runtime;
    private IslandWindow? _island;
    private System.Windows.Forms.NotifyIcon? _tray;
    private HwndSource? _source;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(true, MutexName, out var created);
        if (!created)
        {
            MessageBox.Show("Kojima 已经在运行。", "Kojima", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        AppLog.Init("shell");
        HookCrashLogging();
        ThemeCatalog.Reload();

        _runtime = new AppRuntime(Dispatcher);
        AppRuntime.Current = _runtime;

        _island = new IslandWindow(_runtime);
        _island.Loaded += (_, _) => HookHotkeys();
        _island.Show();
        _runtime.Weather.Start();
        _runtime.StartFeatures();

        CreateTray();
        ApplyDevHooks();
        AppLog.Info("Kojima 启动完成");
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
            }));
        }

        if (Environment.GetEnvironmentVariable("KOJIMA_DEMO") == "1" && _island is not null)
        {
            _island.ApplyDemoData();
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
                    var text = await Kojima.Shell.Services.TranslationService.TranslateAsync(
                        probe, _runtime.Config.Translate).ConfigureAwait(false);
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
    }

    private void CreateTray()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("打开面板", null, (_, _) => _island?.Toggle());
        menu.Items.Add("监控", null, (_, _) => OpenMonitor());
        menu.Items.Add("设置", null, (_, _) => OpenSettings());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApp());

        _tray = new System.Windows.Forms.NotifyIcon
        {
            Text = "Kojima",
            Visible = true,
            ContextMenuStrip = menu,
            Icon = LoadTrayIcon(),
        };
        _tray.MouseClick += (_, args) =>
        {
            if (args.Button == System.Windows.Forms.MouseButtons.Left)
            {
                _island?.Toggle();
            }
        };
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "assets", "kojima.ico");
            if (File.Exists(path))
            {
                return new System.Drawing.Icon(path);
            }

            var stream = typeof(App).Assembly.GetManifestResourceStream("Kojima.ico");
            if (stream is not null)
            {
                return new System.Drawing.Icon(stream);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("托盘图标加载失败：" + ex.Message);
        }

        return System.Drawing.SystemIcons.Application;
    }

    private void ExitApp()
    {
        _exiting = true;
        _island?.Close();

        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }

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
            _tray?.Dispose();
            try
            {
                _runtime?.DisposeAsync().AsTask().Wait(2000);
            }
            catch (Exception ex)
            {
                AppLog.Warn("退出清理失败：" + ex.Message);
            }
        }

        _mutex?.Dispose();
        base.OnExit(e);
    }
}
