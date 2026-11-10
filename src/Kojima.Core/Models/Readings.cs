namespace Kojima.Core.Models;

/// <summary>
/// 一次读数：要么有值，要么带原因不可用；界面用破折号显示不可用，而不是编造 0。
/// 结构参考 BrimDeck（Apache-2.0）的 Reading&lt;T&gt;，仅为 Kojima 的监控页裁剪。
/// </summary>
public readonly struct Reading<T>
{
    private readonly T _value;
    private readonly string? _reason;

    private Reading(T value, bool known, string? reason)
    {
        _value = value;
        HasValue = known;
        _reason = reason;
    }

    public bool HasValue { get; }

    public string Reason => HasValue ? "" : _reason ?? "尚未读取";

    public static Reading<T> Of(T value) => new(value, true, null);

    public static Reading<T> Unavailable(string reason) => new(default!, false, reason);

    public bool TryGet(out T value)
    {
        value = _value;
        return HasValue;
    }

    public T? OrDefault => HasValue ? _value : default;

    public static implicit operator Reading<T>(T value) => Of(value);
}

/// <summary>前台程序提交的帧率；Path/Refresh/History 与 BrimDeck 的 FrameSample 对齐。</summary>
public sealed record FrameSample(double PerSecond, string Program)
{
    public string? Path { get; init; }
    public double? Refresh { get; init; }
    public IReadOnlyList<double> History { get; init; } = [];
}
