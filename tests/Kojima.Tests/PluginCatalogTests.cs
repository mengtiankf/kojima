using System.Diagnostics;
using System.Text.Json;
using Kojima.Core.Plugins;
using Xunit;

namespace Kojima.Tests;

public sealed class PluginCatalogTests
{
    [Fact]
    public void LoadsAndDiscoversIndependentPlugins()
    {
        var root = Path.Combine(Path.GetTempPath(), "kojima-plugin-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var directory = Path.Combine(root, "sample");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "Sample.dll"), "test");
            var manifest = new PluginManifest
            {
                Id = "sample",
                Name = "Sample",
                Version = "1.3.2",
                Assembly = "Sample.dll",
                EntryType = "Sample.Feature",
            };
            File.WriteAllText(Path.Combine(directory, "plugin.json"), JsonSerializer.Serialize(manifest));

            var loaded = PluginCatalog.Load(Path.Combine(directory, "plugin.json"));
            var discovered = PluginCatalog.Discover(root);

            Assert.Equal("sample", loaded.Manifest.Id);
            Assert.True(File.Exists(loaded.AssemblyPath));
            Assert.Single(discovered);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void RejectsAssemblyPathOutsidePluginDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "kojima-plugin-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "plugin.json");
            File.WriteAllText(path, """
                {
                  "id": "escape",
                  "name": "Escape",
                  "assembly": "../Outside.dll",
                  "entryType": "Sample.Feature"
                }
                """);
            Assert.Throws<InvalidDataException>(() => PluginCatalog.Load(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AllFirstPartyManifestsArePresentAndValid()
    {
        var expected = new[]
        {
            "clipboard", "controls", "media", "messages", "monitor", "spectrum",
            "stocks", "translate", "updates", "usage", "weather",
        };
        var configuration = AppContext.BaseDirectory.Contains("Release", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";
        var plugins = Directory.EnumerateDirectories(RepositoryPath("plugins"))
            .Select(path => Path.Combine(path, "bin", configuration, "net10.0-windows10.0.19041.0", "plugin.json"))
            .Where(File.Exists)
            .Select(PluginCatalog.Load)
            .ToArray();

        Assert.Equal(expected, plugins.Select(p => p.Manifest.Id).Order().ToArray());
        Assert.All(plugins, plugin => Assert.True(File.Exists(plugin.AssemblyPath)));
    }

    [Fact]
    public async Task EveryPluginLoadsInTheSharedHost()
    {
        var configuration = AppContext.BaseDirectory.Contains("Release", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";
        var host = RepositoryPath("src", "Kojima.PluginHost", "bin", configuration,
            "net10.0-windows10.0.19041.0", "Kojima.PluginHost.exe");
        Assert.True(File.Exists(host), host);
        var home = Path.Combine(Path.GetTempPath(), "kojima-plugin-smoke", Guid.NewGuid().ToString("N"));

        foreach (var manifest in Directory.EnumerateDirectories(RepositoryPath("plugins"))
                     .Select(path => Path.Combine(path, "bin", configuration, "net10.0-windows10.0.19041.0", "plugin.json"))
                     .Where(File.Exists))
        {
            var start = new ProcessStartInfo(host)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add("--plugin");
            start.ArgumentList.Add(manifest);
            start.ArgumentList.Add("--selftest");
            start.Environment["KOJIMA_HOME"] = home;
            using var process = Process.Start(start)!;
            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, $"{manifest}\n{output}\n{error}");
        }
    }

    private static string RepositoryPath(params string[] parts)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        return Path.Combine([root, .. parts]);
    }
}
