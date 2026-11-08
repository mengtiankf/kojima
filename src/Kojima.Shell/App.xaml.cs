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
        ThemeCatalog.Reload();

        _runtime = new AppRuntime(Dispatcher);
        AppRuntime.Current = _runtime;
        ThemeRuntime.Apply(_runtime.Config.Island.Theme);

        _island = new IslandWindow(_runtime);
        _island.Loaded += (_, _) => HookHotkeys();
        _island.Show();
        _runtime.Weather.Start();

        CreateTray();
        ApplyDevHooks();
        AppLog.Info("Kojima 启动完成");
    }

    /// <summary>开发/截图钩子：KOJIMA_PREVIEW 指定页面，KOJIMA_SHOT 输出窗口 PNG。</summary>
    private void ApplyDevHooks()
    {
        var preview = Environment.GetEnvironmentVariable("KOJIMA_PREVIEW");
        if (!string.IsNullOrWhiteSpace(preview) && _island is not null)
        {
            _island.ShowPage(preview);
            _island.Expand(true);
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

        if (!NativeMethods.RegisterHotKey(handle, id, modifiers | NativeMethods.MOD_NOREPEAT, key))
        {
            AppLog.Warn($"热键注册失败：{text}");
        }
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

    private void OpenSettings()
    {
        if (_runtime is null)
        {
            return;
        }

        var window = new Settings.SettingsWindow(_runtime);
        window.Saved += Reapply;
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

        ThemeRuntime.Apply(_runtime.Config.Island.Theme);
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
