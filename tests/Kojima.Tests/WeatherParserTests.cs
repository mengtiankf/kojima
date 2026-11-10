using System.Text.Json;
using Kojima.Core.Models;
using Xunit;

namespace Kojima.Tests;

public sealed class WeatherParserTests
{
    private const string Sample = """
    {
      "current_condition": [
        {
          "temp_C": "21",
          "FeelsLikeC": "20",
          "humidity": "55",
          "windspeedKmph": "12",
          "weatherCode": "116",
          "lang_zh": [{ "value": "多云" }]
        }
      ],
      "nearest_area": [{ "areaName": [{ "value": "Hangzhou" }] }],
      "weather": [
        {
          "date": "2026-11-10",
          "maxtempC": "24",
          "mintempC": "15",
          "hourly": [
            {}, {}, {}, {},
            { "weatherCode": "119", "lang_zh": [{ "value": "阴" }] }
          ]
        },
        {
          "date": "2026-11-11",
          "maxtempC": "22",
          "mintempC": "14",
          "hourly": [
            {}, {}, {}, {},
            { "weatherCode": "296", "weatherDesc": [{ "value": "Light rain" }] }
          ]
        }
      ]
    }
    """;

    [Fact]
    public void ParsesCurrentConditionsAndForecast()
    {
        using var document = JsonDocument.Parse(Sample);
        var info = WeatherParser.Parse(document.RootElement);

        Assert.NotNull(info);
        Assert.Equal(21, info!.Temperature);
        Assert.Equal("多云", info.Description);
        Assert.Equal("Hangzhou", info.City);
        Assert.Equal(2, info.Days.Count);
        Assert.Equal("阴", info.Days[0].Description);
        Assert.Equal("小雨", info.Days[1].Description);
        Assert.False(string.IsNullOrEmpty(info.Glyph));
        Assert.Equal("21°", info.TemperatureText);
    }

    [Theory]
    [InlineData("Patchy rain nearby", "小雨")]
    [InlineData("Heavy snow", "大雪")]
    [InlineData("Sunny", "晴")]
    [InlineData("晴", "晴")]
    [InlineData("", "")]
    public void TranslatesEnglishDescriptions(string input, string expected) =>
        Assert.Equal(expected, WeatherParser.Zh(input));

    [Fact]
    public void ReturnsNullWithoutCurrentConditions()
    {
        using var document = JsonDocument.Parse("{}");
        Assert.Null(WeatherParser.Parse(document.RootElement));
    }
}
