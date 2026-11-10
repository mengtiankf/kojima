namespace Kojima.Core.Models;

/// <summary>媒体子进程通过 JSON 传回的播放快照（封面为原始图片字节）。</summary>
public sealed record MediaSnapshot(
    string Title,
    string Artist,
    string Album,
    string AppId,
    bool IsPlaying,
    long PositionMs,
    long DurationMs,
    byte[]? Artwork);
