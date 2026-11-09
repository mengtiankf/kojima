using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;
using Kojima.Core.Config;

namespace Kojima.Shell.Services;

/// <summary>当前系统媒体会话的快照。</summary>
public sealed record MediaInfo(
    string Title,
    string Artist,
    string Album,
    string AppId,
    bool IsPlaying,
    TimeSpan Position,
    TimeSpan Duration,
    ImageSource? Artwork);

/// <summary>系统媒体会话：读取 SMTC 的正在播放信息，并提供播放控制。</summary>
public sealed class MediaSessionService : IDisposable
{
    private readonly object _gate = new();
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private Timer? _timer;
    private int _polling;
    private MediaInfo? _current;
    private string _artKey = "";
    private ImageSource? _art;
    private bool _started;

    public MediaInfo? Current => _current;

    /// <summary>媒体信息变化（可能来自后台线程，UI 侧自行 Dispatcher 转发）。</summary>
    public event Action? Changed;

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _timer = new Timer(_ => _ = PollAsync(), null, 0, 1000);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        _started = false;
    }

    public async Task TogglePlayPauseAsync()
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        try
        {
            await session.TryTogglePlayPauseAsync();
            await PollAsync();
        }
        catch (Exception ex)
        {
            AppLog.Warn("媒体播放/暂停失败：" + ex.Message);
        }
    }

    public async Task NextAsync()
    {
        try
        {
            if (_session is { } session)
            {
                await session.TrySkipNextAsync();
                await PollAsync();
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("媒体下一首失败：" + ex.Message);
        }
    }

    public async Task PreviousAsync()
    {
        try
        {
            if (_session is { } session)
            {
                await session.TrySkipPreviousAsync();
                await PollAsync();
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("媒体上一首失败：" + ex.Message);
        }
    }

    public async Task SeekAsync(TimeSpan position)
    {
        try
        {
            if (_session is { } session)
            {
                await session.TryChangePlaybackPositionAsync(position.Ticks);
                await PollAsync();
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("媒体跳转失败：" + ex.Message);
        }
    }

    private async Task PollAsync()
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1)
        {
            return;
        }

        try
        {
            _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var session = _manager.GetCurrentSession();
            if (session is null)
            {
                foreach (var candidate in _manager.GetSessions())
                {
                    try
                    {
                        if (candidate.GetPlaybackInfo().PlaybackStatus ==
                            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                        {
                            session = candidate;
                            break;
                        }
                    }
                    catch
                    {
                        // 单个会话读取失败不影响其它会话
                    }
                }
            }

            if (session is null)
            {
                SetCurrent(null);
                return;
            }

            _session = session;
            var props = await session.TryGetMediaPropertiesAsync();
            var playback = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();

            var title = props?.Title ?? "";
            var artist = props?.Artist ?? "";
            var album = props?.AlbumTitle ?? "";
            if (title.Length == 0 && artist.Length == 0)
            {
                SetCurrent(null);
                return;
            }

            var duration = timeline.EndTime - timeline.StartTime;
            if (duration < TimeSpan.Zero)
            {
                duration = TimeSpan.Zero;
            }

            var position = timeline.Position - timeline.StartTime;
            if (position < TimeSpan.Zero)
            {
                position = TimeSpan.Zero;
            }

            if (position > duration && duration > TimeSpan.Zero)
            {
                position = duration;
            }

            var playing = playback?.PlaybackStatus ==
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            var art = await LoadArtworkAsync(props, title, artist);
            SetCurrent(new MediaInfo(title, artist, album, session.SourceAppUserModelId ?? "", playing, position, duration, art));
        }
        catch (Exception ex)
        {
            AppLog.Warn("读取媒体会话失败：" + ex.Message);
            SetCurrent(null);
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    private async Task<ImageSource?> LoadArtworkAsync(
        GlobalSystemMediaTransportControlsSessionMediaProperties? props,
        string title,
        string artist)
    {
        var key = title + "\u0001" + artist;
        if (key == _artKey)
        {
            return _art;
        }

        _artKey = key;
        _art = null;
        try
        {
            var reference = props?.Thumbnail;
            if (reference is null)
            {
                return null;
            }

            using var stream = await reference.OpenReadAsync();
            var size = (uint)stream.Size;
            if (size == 0 || size > 12 * 1024 * 1024)
            {
                return null;
            }

            var bytes = new byte[size];
            using (var reader = new DataReader(stream.GetInputStreamAt(0)))
            {
                await reader.LoadAsync(size);
                reader.ReadBytes(bytes);
            }

            using var memory = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = memory;
            bitmap.EndInit();
            bitmap.Freeze();
            _art = bitmap;
        }
        catch
        {
            _art = null;
        }

        return _art;
    }

    private void SetCurrent(MediaInfo? info)
    {
        MediaInfo? previous;
        lock (_gate)
        {
            previous = _current;
            _current = info;
        }

        if (Same(previous, info))
        {
            return;
        }

        Changed?.Invoke();
    }

    private static bool Same(MediaInfo? a, MediaInfo? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        return a.Title == b.Title
            && a.Artist == b.Artist
            && a.IsPlaying == b.IsPlaying
            && a.Duration == b.Duration
            && (int)a.Position.TotalSeconds == (int)b.Position.TotalSeconds;
    }

    public void Dispose()
    {
        Stop();
        _session = null;
        _manager = null;
    }
}
