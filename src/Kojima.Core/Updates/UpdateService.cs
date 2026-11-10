using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Kojima.Core.Updates;

public sealed record UpdateAsset(string Name, string Url, long Size);

public sealed record UpdateRelease(
    string Tag,
    string Version,
    string PageUrl,
    string Notes,
    List<UpdateAsset> Assets);

/// <summary>GitHub Releases 检查与下载；不依赖 WPF，便于测试。</summary>
public static class UpdateService
{
    public const string Repository = "mengtiankf/kojima";
    private const string LatestApi = "https://api.github.com/repos/" + Repository + "/releases/latest";

    public static async Task<UpdateRelease?> CheckAsync(
        Version current,
        HttpClient? http = null,
        CancellationToken ct = default)
    {
        http ??= CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestApi);
        request.Headers.UserAgent.ParseAdd("Kojima/1.2.7");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"更新检查失败：HTTP {(int)response.StatusCode}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var root = document.RootElement;
        var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() ?? "" : "";
        var version = NormalizeVersion(tag);
        if (version.Length == 0 || !TryParseVersion(version, out var latest) || latest <= current)
        {
            return null;
        }

        var assets = new List<UpdateAsset>();
        if (root.TryGetProperty("assets", out var assetList) && assetList.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assetList.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                var url = asset.TryGetProperty("browser_download_url", out var urlElement)
                    ? urlElement.GetString()
                    : null;
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url))
                {
                    continue;
                }

                var size = asset.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var bytes)
                    ? bytes
                    : 0;
                assets.Add(new UpdateAsset(name, url, size));
            }
        }

        return new UpdateRelease(
            tag,
            version,
            root.TryGetProperty("html_url", out var page) ? page.GetString() ?? "" : "",
            root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
            assets);
    }

    public static async Task DownloadAsync(
        UpdateAsset asset,
        string path,
        IProgress<double>? progress = null,
        IProgress<long>? bytes = null,
        CancellationToken ct = default)
    {
        using var http = CreateClient(TimeSpan.FromMinutes(30));
        using var response = await http
            .GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? asset.Size;

        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var temp = path + ".part";
        await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var target = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920,
                         useAsync: true))
        {
            var buffer = new byte[81920];
            long written = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                written += read;
                bytes?.Report(written);
                if (total > 0)
                {
                    progress?.Report((double)written / total);
                }
            }
        }

        File.Move(temp, path, overwrite: true);
    }

    public static async Task<string?> TryReadSha256Async(
        UpdateRelease release,
        UpdateAsset asset,
        HttpClient? http = null,
        CancellationToken ct = default)
    {
        var checksum = release.Assets.FirstOrDefault(a => a.Name.Equals(asset.Name + ".sha256",
            StringComparison.OrdinalIgnoreCase));
        if (checksum is null)
        {
            return null;
        }

        http ??= CreateClient();
        var text = await http.GetStringAsync(checksum.Url, ct).ConfigureAwait(false);
        var token = text.Trim().Split(' ', '\t', '\r', '\n')[0];
        return token.Length == 64 ? token : null;
    }

    public static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static string NormalizeVersion(string tag)
    {
        var value = tag.Trim();
        if (value.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            value = value[1..];
        }

        var dash = value.IndexOf('-');
        return dash > 0 ? value[..dash] : value;
    }

    public static bool TryParseVersion(string text, out Version version)
    {
        var parts = text.Split('.');
        if (parts.Length is 2 or 3 &&
            int.TryParse(parts[0], out var major) &&
            int.TryParse(parts[1], out var minor) &&
            (parts.Length == 2 || int.TryParse(parts[2], out _)))
        {
            var build = parts.Length == 3 && int.TryParse(parts[2], out var parsed) ? parsed : 0;
            // 用四段版本比较，避免 1.2.7 被当成小于程序集里的 1.2.7.0。
            version = new Version(major, minor, build, 0);
            return true;
        }

        version = new Version(0, 0, 0, 0);
        return false;
    }

    private static HttpClient CreateClient(TimeSpan? timeout = null) =>
        new()
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(20),
        };
}
