namespace Kojima.Core.Models;

/// <summary>一条 QQ / 微信 / 邮件等系统通知（只保留发送者或首行文字，不读正文）。</summary>
public sealed record MessageItem(string App, string Kind, string Sender, DateTime At, string Source)
{
    public string TimeText => At.ToString("HH:mm");
    public string DayText => At.Date == DateTime.Today ? "今天" : At.ToString("M月d日");
}
