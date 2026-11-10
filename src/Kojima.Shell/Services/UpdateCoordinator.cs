using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using Kojima.Core.Config;
using Kojima.Core.Updates;

namespace Kojima.Shell.Services;

/// <summary>
/// 自动更新：GitHub Releases 检查、安装包/便携包下载、便携版就地替换。
/// 安装版交给 Inno Setup 静默升级；便携版用一次性脚本等在退出后覆盖文件。
/// </summary>
public sealed class UpdateCoordinator
{
    private readonly NotifyCenter _notify;
    private readonly AgentSupervisor _agents;
    private bool _busy;

    public UpdateCoordinator(NotifyCenter notify, AgentSupervisor agents)
    {
        _notify = notify;
        _agents = agents;
    }

    public event Action<string>? StatusChanged;

    public UpdateRelease? Pending { get; private set; }

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);

    public static string CurrentVersionText
    {
        get
        {
            var version = CurrentVersion;
            return version.Build > 0 ? $"{version.Major}.{version.Minor}.{version.Build}" : $"{version.Major}.{version.Minor}";
        }
    }

    /// <summary>便携版：程序不在 %LOCALAPPDATA%\Programs\Kojima 下。</summary>
    public static bool IsPortable
    {
        get
        {
            var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            var installed = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "Kojima").TrimEnd(Path.DirectorySeparatorChar);
            return !baseDir.Equals(installed, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>手动或自动检查；返回新版本说明，没有更新返回 null。</summary>
    public async Task<UpdateRelease?> CheckAsync(bool manual)
    {
        if (_busy)
        {
            return Pending;
        }

        _busy = true;
        try
        {
            AppLog.Info("开始检查更新：" + CurrentVersionText);
            StatusChanged?.Invoke("正在检查更新…");
            var release = await _agents.RequestAsync<UpdateRelease?>("updates", "updates.check",
                new { currentVersion = CurrentVersionText }, 40000).ConfigureAwait(false);
            Pending = release;
            if (release is null)
            {
                StatusChanged?.Invoke("已是最新版本");
                if (manual)
                {
                    _notify.Push("检查更新", $"当前 {CurrentVersionText} 已是最新版本。", "other");
                }

                return null;
            }

            StatusChanged?.Invoke($"发现新版本 {release.Version}");
            _notify.Push("发现新版本", $"Kojima {release.Version} 已发布，可在设置 → 关于中更新。", "other");
            return release;
        }
        catch (Exception ex)
        {
            AppLog.Warn("更新检查失败：" + ex.Message);
            StatusChanged?.Invoke("检查更新失败");
            if (manual)
            {
                _notify.Push("检查更新失败", ex.Message, "warn");
            }

            return null;
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>下载并启动更新；成功返回 true（调用方随后退出应用）。</summary>
    public async Task<bool> InstallAsync(UpdateRelease release, IProgress<double>? progress = null)
    {
        var asset = PickAsset(release);
        if (asset is null)
        {
            StatusChanged?.Invoke("该版本没有可用的安装包");
            _notify.Push("更新失败", "该版本没有可用的安装包或便携包。", "warn");
            return false;
        }

        string path;
        try
        {
            var folder = Path.Combine(ConfigStore.RootDirectory, "updates");
            Directory.CreateDirectory(folder);
            path = Path.Combine(folder, asset.Name);
            StatusChanged?.Invoke($"正在下载 {asset.Name}…");
            progress?.Report(0);
            var shaUrl = release.Assets.FirstOrDefault(a => a.Name.Equals(asset.Name + ".sha256", StringComparison.OrdinalIgnoreCase))?.Url ?? "";
            var result = await _agents.RequestAsync<UpdateDownloadResult>("updates", "updates.download",
                new { url = asset.Url, name = asset.Name, size = asset.Size, path, shaUrl }, 600000).ConfigureAwait(false);
            progress?.Report(1);
            if (result is null || !result.Ok)
            {
                StatusChanged?.Invoke("安装包下载失败");
                _notify.Push("更新失败", result?.Error ?? "下载失败", "warn");
                return false;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("更新下载失败：" + ex.Message);
            StatusChanged?.Invoke("下载失败");
            _notify.Push("更新失败", ex.Message, "warn");
            return false;
        }

        try
        {
            if (asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && IsPortable)
            {
                return ApplyPortable(path);
            }

            if (!asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                StatusChanged?.Invoke("该版本没有可用的安装程序");
                return false;
            }

            Process.Start(new ProcessStartInfo(path, "/SILENT /CLOSEAPPLICATIONS /NORESTART")
            {
                UseShellExecute = true,
            });
            StatusChanged?.Invoke("安装程序已启动，正在退出…");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("启动更新失败：" + ex.Message);
            StatusChanged?.Invoke("启动更新失败");
            _notify.Push("更新失败", ex.Message, "warn");
            return false;
        }
    }

    private UpdateAsset? PickAsset(UpdateRelease release)
    {
        if (IsPortable)
        {
            var zip = release.Assets.FirstOrDefault(a =>
                a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                a.Name.Contains("portable", StringComparison.OrdinalIgnoreCase));
            if (zip is not null)
            {
                return zip;
            }
        }

        return release.Assets.FirstOrDefault(a =>
            a.Name.StartsWith("KojimaSetup", StringComparison.OrdinalIgnoreCase) &&
            a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
    }

    private bool ApplyPortable(string zipPath)
    {
        var target = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var staging = Path.Combine(Path.GetDirectoryName(zipPath)!, "staging-" + Guid.NewGuid().ToString("N")[..8]);
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, recursive: true);
        }

        ZipFile.ExtractToDirectory(zipPath, staging);
        var script = Path.Combine(Path.GetDirectoryName(zipPath)!, "apply-update.cmd");
        var exe = Path.Combine(target, "Kojima.exe");
        var robocopy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "robocopy.exe");
        var lines = new[]
        {
            "@echo off",
            "timeout /t 2 /nobreak >nul",
            $"taskkill /pid {Environment.ProcessId} /f >nul 2>&1",
            $"\"{robocopy}\" \"{staging}\" \"{target}\" /E /IS /IT /R:1 /W:1 >nul",
            $"rd /s /q \"{staging}\" >nul 2>&1",
            $"start \"\" \"{exe}\"",
            "del \"%~f0\" >nul 2>&1",
        };
        File.WriteAllLines(script, lines);
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        StatusChanged?.Invoke("正在退出并替换便携版文件…");
        return true;
    }

    public void ShutdownForUpdate()
    {
        Application.Current?.Shutdown();
    }
}

internal sealed record UpdateDownloadResult(bool Ok, string? Error);
