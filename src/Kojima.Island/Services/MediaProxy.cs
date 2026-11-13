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
    private LyricsResult? _lyrics;
    private string _lyricsKey = "";
    private bool _lyricsLoading;
    private readonly DispatcherTimer _refreshTimer;
    private int _refreshing;

    public MediaProxy(AgentSupervisor agents, Dispatcher dispatcher)
    {
        _agents = agents;
        _dispatcher = dispatcher;
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromSeconds(5),
        };
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
        _agents.EventReceived += OnEvent;
    }

    public MediaInfo? Current => _current;

    /// <summary>当前曲目的歌词；没有歌词源时为 null。</summary>
    public LyricsResult? Lyrics => _lyrics;

    /// <summary>按当前进度取一行歌词（无时间轴时取第一行）。</summary>
    public string? CurrentLyricLine
    {
        get
        {
            if (_lyrics is null || _current is null || _lyrics.Lines.Count == 0)
            {
                return null;
            }

            if (!_lyrics.HasTimeline)
            {
                return _lyrics.Lines[0].Text;
            }

            var position = _current.Position.TotalMilliseconds;
            LyricLine? current = null;
            foreach (var line in _lyrics.Lines)
            {
                if (line.TimeMs <= position)
                {
                    current = line;
                }
                else
                {
                    break;
                }
            }

            return current?.Text;
        }
    }

    /// <summary>媒体信息变化（已切回 UI 线程）。</summary>
    public event Action? Changed;

    public void Start()
    {
        _starting ??= StartAsync();
        _refreshTimer.Start();
    }

    public void Stop()
    {
        _refreshTimer.Stop();
        _starting = null;
        if (_agents.IsRunning("media"))
        {
            _ = _agents.RequestAsync<object>("media", "media.stop", null, 5000);
        }
        _lyrics = null;
        _lyricsKey = "";
        Apply(null);
    }

    public async Task RefreshAsync()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1)
        {
            return;
        }

        try
        {
            await CommandAsync("media.state").ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

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

                var key = (snapshot.Title ?? "") + "|" + (snapshot.Artist ?? "");
                if (!key.Equals(_lyricsKey, StringComparison.OrdinalIgnoreCase))
                {
                    _lyricsKey = key;
                    _lyrics = null;
                    if (!string.IsNullOrWhiteSpace(snapshot.Title))
                    {
                        _ = LoadLyricsAsync(snapshot);
                    }
                }
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

    public void ReloadLyrics()
    {
        if (_current is null)
        {
            return;
        }
        _lyricsKey = "";
        _lyrics = null;
        _ = LoadLyricsAsync(new MediaSnapshot(
            _current.Title,
            _current.Artist,
            _current.Album,
            _current.AppId,
            _current.IsPlaying,
            (long)_current.Position.TotalMilliseconds,
            (long)_current.Duration.TotalMilliseconds,
            null));
        Changed?.Invoke();
    }

    private async Task LoadLyricsAsync(MediaSnapshot snapshot)
    {
        if (_lyricsLoading)
        {
            return;
        }

        _lyricsLoading = true;
        try
        {
            var result = await _agents.RequestAsync<LyricsResult?>(
                "media",
                "media.lyrics",
                new
                {
                    title = snapshot.Title ?? "",
                    artist = snapshot.Artist ?? "",
                    durationMs = snapshot.DurationMs,
                    appId = snapshot.AppId ?? "",
                },
                20000).ConfigureAwait(false);
            _ = _dispatcher.BeginInvoke(() =>
            {
                _lyrics = result;
                Changed?.Invoke();
            });
        }
        catch (Exception ex)
        {
            Core.Config.AppLog.Warn("读取歌词失败：" + ex.Message);
        }
        finally
        {
            _lyricsLoading = false;
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
        Stop();
    }
}
