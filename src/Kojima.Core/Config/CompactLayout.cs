namespace Kojima.Core.Config;

public static class CompactLayout
{
    public static IReadOnlyList<string> Available { get; } = ["clock", "date", "weather", "media", "stocks", "usage", "monitor"];

    public static IReadOnlyList<string> Resolve(IslandSettings settings)
    {
        var source = settings.CompactItems ?? LegacyItems(settings.CompactContent);
        return source.Where(key => Available.Contains(key, StringComparer.OrdinalIgnoreCase))
            .Select(key => key.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string LegacyContent(IReadOnlyList<string> items)
    {
        if (items.Count == 0)
        {
            return "none";
        }

        if (items.Count == 1)
        {
            return items[0];
        }

        return items.Count == 2 && items[0] == "clock" && items[1] == "weather" ? "clockWeather" : "custom";
    }

    private static IReadOnlyList<string> LegacyItems(string? content) => content switch
    {
        "clock" => ["clock"],
        "weather" => ["weather"],
        "media" => ["media"],
        "none" => [],
        _ => ["clock", "weather"],
    };
}
