using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Kojima.Core.Ipc;
using Kojima.Core.Models;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Kojima.Agents.Features;

/// <summary>系统媒体会话（SMTC）：轮询当前播放信息，接收播放控制；封面以原始字节回报。</summary>
public sealed class MediaFeature : IAgentFeature
{
    private readonly object _gate = new();
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private AgentPipeServer? _server;
    private Timer? _timer;
    private int _polling;
    private volatile bool _started;
    private MediaSnapshot? _snapshot;
    private string _artKey = "";
    private byte[]? _artwork;

    public bool Busy => false;

    public void Attach(AgentPipeServer server) => _server = server;

    public ValueTask DisposeAsync()
    {
        Stop();
        return ValueTask.CompletedTask;
    }

    public async Task<object?> HandleAsync(string method, JsonElement? args)
    {
        switch (method.ToLowerInvariant())
        {
            case "media.start":
                Start();
                await RefreshAsync(force: true).ConfigureAwait(false);
                return _snapshot;
            case "media.stop":
                Stop();
                return _snapshot;
            case "media.state":
                await RefreshAsync(force: false).ConfigureAwait(false);
                return _snapshot;
            case "media.toggle":
                await ActAsync(session => session.TryTogglePlayPauseAsync()).ConfigureAwait(false);
                return _snapshot;
            case "media.next":
                await ActAsync(session => session.TrySkipNextAsync()).ConfigureAwait(false);
                return _snapshot;
            case "media.previous":
                await ActAsync(session => session.TrySkipPreviousAsync()).ConfigureAwait(false);
                return _snapshot;
            case "media.seek":
                var milliseconds = args is { ValueKind: JsonValueKind.Object } payload && payload.TryGetProperty("ms", out var value)
                    ? value.GetInt64()
                    : 0;
                await SeekAsync(TimeSpan.FromMilliseconds(Math.Max(0, milliseconds))).ConfigureAwait(false);
                return _snapshot;
            case "media.lyrics":
                var title = "";
                var artist = "";
                var duration = 0L;
                if (args is { ValueKind: JsonValueKind.Object } request)
                {
                    if (request.TryGetProperty("title", out var t)) { title = t.GetString() ?? ""; }
                    if (request.TryGetProperty("artist", out var a)) { artist = a.GetString() ?? ""; }
                    if (request.TryGetProperty("durationMs", out var d)) { duration = d.GetInt64(); }
                }
                return await LyricsProvider.FetchAsync(title, artist, duration, CancellationToken.None).ConfigureAwait(false);
            default:
                throw new InvalidOperationException("未知方法：" + method);
        }
    }

    private void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _timer = new Timer(_ => _ = RefreshAsync(force: false), null, 0, 1000);
    }

    private void Stop()
    {
        _started = false;
        _timer?.Dispose();
        _timer = null;
    }

    private async Task ActAsync(Func<GlobalSystemMediaTransportControlsSession, global::Windows.Foundation.IAsyncOperation<bool>> action)
    {
        if (await SessionAsync().ConfigureAwait(false) is not { } session)
        {
            return;
        }

        try
        {
            await action(session).AsTask().ConfigureAwait(false);
            await Task.Delay(120).ConfigureAwait(false);
            await RefreshAsync(force: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Core.Config.AppLog.Warn("媒体控制失败：" + ex.Message);
        }
    }

    private async Task SeekAsync(TimeSpan position)
    {
        if (await SessionAsync().ConfigureAwait(false) is not { } session)
        {
            return;
        }

        try
        {
            await session.TryChangePlaybackPositionAsync(position.Ticks).AsTask().ConfigureAwait(false);
            await Task.Delay(120).ConfigureAwait(false);
            await RefreshAsync(force: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Core.Config.AppLog.Warn("媒体跳转失败：" + ex.Message);
        }
    }

    private async Task<GlobalSystemMediaTransportControlsSession?> SessionAsync()
    {
        if (_manager is null)
        {
            try
            {
                _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Core.Config.AppLog.Warn("连接系统媒体会话失败：" + ex.Message);
                return null;
            }
        }

        if (_session is null)
        {
            try
            {
                _session = _manager.GetCurrentSession()
                           ?? _manager.GetSessions().FirstOrDefault(s => s.GetPlaybackInfo().PlaybackStatus ==
                               GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
            }
            catch
            {
                _session = null;
            }
        }

        return _session;
    }

    private async Task RefreshAsync(bool force)
    {
        if (!_started || Interlocked.Exchange(ref _polling, 1) == 1)
        {
            return;
        }

        try
        {
            var session = await SessionAsync().ConfigureAwait(false);
            if (session is null)
            {
                Publish(null);
                return;
            }

            var properties = await session.TryGetMediaPropertiesAsync().AsTask().ConfigureAwait(false);
            var playback = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();
            var playing = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            var key = properties.Title + "\u001f" + properties.AlbumTitle + "\u001f" + properties.Artist;
            if (force || key != _artKey)
            {
                _artKey = key;
                _artwork = await ReadArtworkAsync(properties.Thumbnail).ConfigureAwait(false);
            }

            var position = timeline.Position - timeline.StartTime;
            if (position < TimeSpan.Zero)
            {
                position = TimeSpan.Zero;
            }

            var duration = timeline.EndTime - timeline.StartTime;
            if (duration < TimeSpan.Zero)
            {
                duration = TimeSpan.Zero;
            }

            Publish(new MediaSnapshot(
                properties.Title ?? "",
                properties.Artist ?? "",
                properties.AlbumTitle ?? "",
                session.SourceAppUserModelId ?? "",
                playing,
                (long)position.TotalMilliseconds,
                (long)duration.TotalMilliseconds,
                _artwork));
        }
        catch (Exception ex)
        {
            Core.Config.AppLog.Warn("读取媒体会话失败：" + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    private static async Task<byte[]?> ReadArtworkAsync(IRandomAccessStreamReference? reference)
    {
        if (reference is null)
        {
            return null;
        }

        try
        {
            using var stream = await reference.OpenReadAsync().AsTask().ConfigureAwait(false);
            using var buffer = new MemoryStream();
            using var input = stream.AsStreamForRead();
            await input.CopyToAsync(buffer).ConfigureAwait(false);
            return buffer.Length is > 0 and <= 4_000_000 ? buffer.ToArray() : null;
        }
        catch
        {
            return null;
        }
    }

    private void Publish(MediaSnapshot? snapshot)
    {
        var changed = !Equals(snapshot, _snapshot);
        if (!changed && snapshot is not null)
        {
            // 位置每秒推进，只有秒级变化才推，避免同一状态重复上报
            changed = _snapshot is null ||
                      Math.Abs(snapshot.PositionMs - _snapshot.PositionMs) >= 1000 ||
                      snapshot.IsPlaying != _snapshot.IsPlaying;
        }

        _snapshot = snapshot;
        if (!changed)
        {
            return;
        }

        lock (_gate)
        {
            _ = _server?.PushAsync("media", snapshot);
        }
    }
}
