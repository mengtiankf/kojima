using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kojima.Core.Plugins;

public sealed class PluginManifest
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public string Assembly { get; set; } = "";
    public string EntryType { get; set; } = "";
    public string Description { get; set; } = "";
}

public sealed record PluginDescriptor(string ManifestPath, PluginManifest Manifest)
{
    public string Directory { get; } = Path.GetDirectoryName(ManifestPath)!;
    public string AssemblyPath => Path.Combine(Directory, Manifest.Assembly);
}

public static class PluginCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static PluginDescriptor Load(string manifestPath)
    {
        var fullPath = Path.GetFullPath(manifestPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("找不到插件清单", fullPath);
        }

        var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(fullPath), JsonOptions)
            ?? throw new InvalidDataException("插件清单为空：" + fullPath);
        Validate(manifest, fullPath);
        var descriptor = new PluginDescriptor(fullPath, manifest);
        if (!File.Exists(descriptor.AssemblyPath))
        {
            throw new FileNotFoundException("找不到插件程序集", descriptor.AssemblyPath);
        }

        return descriptor;
    }

    public static IReadOnlyList<PluginDescriptor> Discover(string pluginsRoot)
    {
        if (!Directory.Exists(pluginsRoot))
        {
            return [];
        }

        var plugins = new List<PluginDescriptor>();
        foreach (var path in Directory.EnumerateFiles(pluginsRoot, "plugin.json", SearchOption.AllDirectories))
        {
            try
            {
                plugins.Add(Load(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                System.Diagnostics.Trace.TraceWarning("跳过无效插件：" + ex.Message);
            }
        }

        return plugins.OrderBy(plugin => plugin.Manifest.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void Validate(PluginManifest manifest, string path)
    {
        if (!IsValidId(manifest.Id))
        {
            throw new InvalidDataException("插件 ID 无效：" + path);
        }

        if (string.IsNullOrWhiteSpace(manifest.Name) || string.IsNullOrWhiteSpace(manifest.EntryType) ||
            string.IsNullOrWhiteSpace(manifest.Assembly) ||
            !string.Equals(Path.GetFileName(manifest.Assembly), manifest.Assembly, StringComparison.Ordinal) ||
            !manifest.Assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("插件名称、入口类型或程序集无效：" + path);
        }
    }

    private static bool IsValidId(string id) =>
        id.Length is >= 2 and <= 32 && char.IsAsciiLetterLower(id[0]) &&
        id.Skip(1).All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');
}
