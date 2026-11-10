using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Kojima.Core.Ipc;
using Kojima.Core.Models;

namespace Kojima.Island.Services;

/// <summary>当前系统媒体会话的快照（封面已转换为 WPF 图像）。</summary>
public sealed record MediaInfo(
    string Title,
    string Artist,
    string Album,
    string AppId,
    bool IsPlaying,
    TimeSpan Position,
    TimeSpan Duration,
    ImageSource? Artwork);

/// <summary>媒体外壳代理：SMTC 轮询在 media 子进程，这里只保留快照并转发控制命令。</summary>
public sealed class MediaProxy : IDisposable
{
    private readonly AgentSupervisor _agents;
    private readonly Dispatcher _dispatcher;
    private MediaInfo? _current;
    private Task? _starting;

    public MediaProxy(AgentSupervisor agents, Dispatcher dispatcher)
    {
        _agents = agents;
        _dispatcher = dispatcher;
        _agents.EventReceived += OnEvent;
    }

    public MediaInfo? Current => _current;

    /// <summary>媒体信息变化（已切回 UI 线程）。</summary>
    public event Action? Changed;

    public void Start() => _starting ??= StartAsync();

    public void Stop() => _ = _agents.RequestAsync<object>("media", "media.stop", null, 5000);

    public async Task TogglePlayPauseAsync() => await CommandAsync("media.toggle").ConfigureAwait(false);

    public async Task NextAsync() => await CommandAsync("media.next").ConfigureAwait(false);

    public async Task PreviousAsync() => await CommandAsync("media.previous").ConfigureAwait(false);

    public async Task SeekAsync(TimeSpan position) =>
        await CommandAsync("media.seek", new { ms = (long)position.TotalMilliseconds }).ConfigureAwait(false);

    private async Task StartAsync()
    {
        try
        {
            var snapshot = await _agents.RequestAsync<MediaSnapshot>("media", "media.start", null, 20000).ConfigureAwait(false);
            Apply(snapshot);
        }
        catch (Exception ex)
        {
            Core.Config.AppLog.Warn("启动媒体子进程失败：" + ex.Message);
        }
    }

    private async Task CommandAsync(string method, object? args = null)
    {
        try
        {
            var snapshot = await _agents.RequestAsync<MediaSnapshot>("media", method, args, 8000).ConfigureAwait(false);
            Apply(snapshot);
        }
        catch (Exception ex)
        {
            Core.Config.AppLog.Warn("媒体命令失败：" + ex.Message);
        }
    }

    private void OnEvent(string feature, string name, JsonElement data)
    {
        if (feature != "media" || name != "media" || data.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        try
        {
            Apply(data.Deserialize<MediaSnapshot>(IpcJson.Options));
        }
        catch
        {
            // 忽略坏事件
        }
    }

    private void Apply(MediaSnapshot? snapshot)
    {
        void Update()
        {
            if (snapshot is null)
            {
                _current = null;
            }
            else
            {
                _current = new MediaInfo(
                    snapshot.Title ?? "",
                    snapshot.Artist ?? "",
                    snapshot.Album ?? "",
                    snapshot.AppId ?? "",
                    snapshot.IsPlaying,
                    TimeSpan.FromMilliseconds(snapshot.PositionMs),
                    TimeSpan.FromMilliseconds(snapshot.DurationMs),
                    ToImage(snapshot.Artwork));
            }

            Changed?.Invoke();
        }

        if (_dispatcher.CheckAccess())
        {
            Update();
        }
        else
        {
            _dispatcher.BeginInvoke(Update);
        }
    }

    private static ImageSource? ToImage(byte[]? bytes)
    {
        if (bytes is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _agents.EventReceived -= OnEvent;
        if (_starting is not null)
        {
            _ = _agents.RequestAsync<object>("media", "media.stop", null, 3000);
        }
    }
}
