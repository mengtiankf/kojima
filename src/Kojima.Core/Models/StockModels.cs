namespace Kojima.Core.Models;

/// <summary>一条行情。价格与涨跌幅为空表示该字段暂不可用。</summary>
public sealed record StockQuote(string Code, string Name, double? Price, double? Percent, double? Previous);

/// <summary>搜索联想结果，Code 已归一化为腾讯风格（sh600519 / sz000001 / hk00700）。</summary>
public sealed record StockSuggestion(string Code, string Name);
