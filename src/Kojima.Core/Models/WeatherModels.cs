using System.Globalization;
using System.Text.Json;

namespace Kojima.Core.Models;

/// <summary>wttr.in 天气预报与当前实况。解析放在子进程，外壳只负责展示。</summary>
public sealed class WeatherDay
{
    public DateOnly Date { get; init; }

    public int Code { get; init; }

    public double? Max { get; init; }

    public double? Min { get; init; }

    public string Description { get; init; } = "";

    public string Glyph { get; init; } = "";
}

public sealed class WeatherInfo
{
    public double? Temperature { get; init; }

    public double? FeelsLike { get; init; }

    public double? Humidity { get; init; }

    public double? WindKph { get; init; }

    public IReadOnlyList<WeatherDay> Days { get; init; } = [];

    public string Description { get; init; } = "";

    public string City { get; set; } = "";

    public string Glyph { get; init; } = "";

    public int Code { get; init; }

    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;

    public string TemperatureText => Temperature is { } value ? $"{value:0}°" : "--°";
}

/// <summary>把 wttr.in 的 j1 JSON 转成模型。天气码到图标、英文描述到中文的映射也在这里。</summary>
public static class WeatherParser
{
    public static WeatherInfo? Parse(JsonElement root)
    {
        if (!root.TryGetProperty("current_condition", out var conditions) || conditions.GetArrayLength() == 0)
        {
            return null;
        }

        var current = conditions[0];
        var temp = current.TryGetProperty("temp_C", out var t) && double.TryParse(t.GetString(), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : double.NaN;

        var desc = "";
        if (current.TryGetProperty("lang_zh", out var lang) && lang.GetArrayLength() > 0
            && lang[0].TryGetProperty("value", out var value))
        {
            desc = value.GetString() ?? "";
        }

        if (string.IsNullOrWhiteSpace(desc)
            && current.TryGetProperty("weatherDesc", out var english) && english.GetArrayLength() > 0
            && english[0].TryGetProperty("value", out var englishValue))
        {
            desc = englishValue.GetString() ?? "";
        }

        var city = "";
        if (root.TryGetProperty("nearest_area", out var areas) && areas.GetArrayLength() > 0
            && areas[0].TryGetProperty("areaName", out var names) && names.GetArrayLength() > 0
            && names[0].TryGetProperty("value", out var areaName))
        {
            city = areaName.GetString() ?? "";
        }

        var code = 0;
        if (current.TryGetProperty("weatherCode", out var wc) && int.TryParse(wc.GetString(), out var parsedCode))
        {
            code = parsedCode;
        }

        desc = Zh(desc);
        var days = new List<WeatherDay>();
        if (root.TryGetProperty("weather", out var weather) && weather.ValueKind == JsonValueKind.Array)
        {
            foreach (var day in weather.EnumerateArray())
            {
                var max = Num(day, "maxtempC");
                var min = Num(day, "mintempC");
                var dateText = day.TryGetProperty("date", out var d) ? d.GetString() : null;
                var dayCode = 0;
                var dayDesc = "";
                if (day.TryGetProperty("hourly", out var hourly) && hourly.ValueKind == JsonValueKind.Array
                    && hourly.GetArrayLength() > 4)
                {
                    var noon = hourly[4];
                    if (noon.TryGetProperty("weatherCode", out var dwc) && int.TryParse(dwc.GetString(), out var dc))
                    {
                        dayCode = dc;
                    }

                    if (noon.TryGetProperty("lang_zh", out var dl) && dl.GetArrayLength() > 0
                        && dl[0].TryGetProperty("value", out var dv))
                    {
                        dayDesc = dv.GetString() ?? "";
                    }

                    if (string.IsNullOrWhiteSpace(dayDesc)
                        && noon.TryGetProperty("weatherDesc", out var de) && de.GetArrayLength() > 0
                        && de[0].TryGetProperty("value", out var dev))
                    {
                        dayDesc = dev.GetString() ?? "";
                    }
                }

                days.Add(new WeatherDay
                {
                    Date = DateOnly.TryParse(dateText, CultureInfo.InvariantCulture, out var parsedDate)
                        ? parsedDate
                        : DateOnly.FromDateTime(DateTime.Now.AddDays(days.Count)),
                    Max = max,
                    Min = min,
                    Description = Zh(dayDesc),
                    Code = dayCode,
                    Glyph = GlyphFor(dayCode),
                });
            }
        }

        return new WeatherInfo
        {
            Temperature = double.IsNaN(temp) ? null : temp,
            Description = desc,
            City = city,
            Glyph = GlyphFor(code),
            Code = code,
            FeelsLike = Num(current, "FeelsLikeC"),
            Humidity = Num(current, "humidity"),
            WindKph = Num(current, "windspeedKmph"),
            Days = days,
            UpdatedAt = DateTimeOffset.Now,
        };
    }

    /// <summary>把 wttr.in 的英文天气描述转成中文（接口偶尔不给 lang_zh）。</summary>
    public static string Zh(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        if (text.Any(c => c >= 0x4E00 && c <= 0x9FFF))
        {
            return text.Trim();
        }

        var t = text.Trim().ToLowerInvariant();
        return t switch
        {
            var x when x.Contains("thunder") => "雷阵雨",
            var x when x.Contains("heavy rain") => "大雨",
            var x when x.Contains("light rain") || x.Contains("patchy rain") || x.Contains("drizzle") => "小雨",
            var x when x.Contains("rain") || x.Contains("shower") => "雨",
            var x when x.Contains("heavy snow") || x.Contains("blizzard") => "大雪",
            var x when x.Contains("light snow") || x.Contains("patchy snow") || x.Contains("sleet") => "小雪",
            var x when x.Contains("snow") || x.Contains("ice") => "雪",
            var x when x.Contains("freezing") || x.Contains("fog") => "雾凇",
            var x when x.Contains("mist") || x.Contains("haze") => "薄雾",
            var x when x.Contains("overcast") => "阴",
            var x when x.Contains("partly cloudy") || x.Contains("partly") => "多云",
            var x when x.Contains("cloudy") => "多云",
            var x when x.Contains("sunny") => "晴",
            var x when x.Contains("clear") => "晴",
            var x when x.Contains("wind") => "大风",
            _ => text.Trim(),
        };
    }

    private static double? Num(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && double.TryParse(value.GetString(), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    /// <summary>把 wttr.in 天气码映射到 Segoe Fluent Icons。</summary>
    private static string GlyphFor(int code) => code switch
    {
        113 => "\uE706", // 晴
        116 => "\uE75E", // 多云
        119 or 122 => "\uE753", // 阴
        143 or 248 or 260 => "\uE9CA", // 雾
        176 or 263 or 266 or 281 or 284 or 293 or 296 or 299 or 302 or 305 or 308
            or 311 or 314 or 317 or 320 or 353 or 356 or 359 => "\uE9C5", // 雨
        179 or 182 or 185 or 227 or 230 or 323 or 326 or 329 or 332 or 335 or 338
            or 368 or 371 or 374 or 377 => "\uE9C8", // 雪
        200 or 386 or 389 or 392 or 395 => "\uE9C4", // 雷
        _ => "\uE753",
    };
}
