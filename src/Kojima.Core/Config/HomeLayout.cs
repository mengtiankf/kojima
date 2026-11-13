namespace Kojima.Core.Config;

public static class HomeLayout
{
    public static IReadOnlyList<string> Default { get; } = ["media", "weather", "clock", "actions"];

    public static IReadOnlyList<string> Available { get; } =
        ["media", "weather", "clock", "actions", "stocks", "usage", "monitor"];

    public static IReadOnlyList<string> Resolve(IslandSettings settings)
    {
        var source = settings.HomeCards ?? Default;
        return source.Where(key => Available.Contains(key, StringComparer.OrdinalIgnoreCase))
            .Select(key => key.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
