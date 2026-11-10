using System.Net;
using System.Net.Sockets;
using System.Text;
using Kojima.Core.Updates;
using Xunit;

namespace Kojima.Tests;

public sealed class UpdateServiceTests
{
    [Theory]
    [InlineData("v1.2.8", "1.2.8")]
    [InlineData("1.2.8", "1.2.8")]
    [InlineData("v2.0", "2.0")]
    [InlineData("1.3.0-beta.2", "1.3.0")]
    public void NormalizesReleaseTags(string tag, string expected)
    {
        Assert.Equal(expected, UpdateService.NormalizeVersion(tag));
    }

    [Fact]
    public void ParsesFourPartVersionSoEqualReleasesAreNotNewer()
    {
        Assert.True(UpdateService.TryParseVersion("1.2.7", out var parsed));
        Assert.Equal(new Version(1, 2, 7, 0), parsed);
        Assert.False(parsed > new Version(1, 2, 7, 0));
        Assert.True(UpdateService.TryParseVersion("1.2.8", out var newer));
        Assert.True(newer > new Version(1, 2, 7, 0));
    }

    [Fact]
    public void RejectsMalformedVersions()
    {
        Assert.False(UpdateService.TryParseVersion("nightly", out _));
        Assert.False(UpdateService.TryParseVersion("1", out _));
    }

    [Fact]
    public async Task DownloadReportsByteProgress()
    {
        var payload = new byte[384 * 1024];
        Random.Shared.NextBytes(payload);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var request = new MemoryStream();
            var buffer = new byte[4096];
            while (request.Length < 8192)
            {
                var read = await stream.ReadAsync(buffer);
                if (read == 0)
                {
                    break;
                }

                request.Write(buffer, 0, read);
                if (Encoding.ASCII.GetString(request.ToArray()).Contains("\r\n\r\n"))
                {
                    break;
                }
            }

            var header = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header);
            await stream.WriteAsync(payload);
            await stream.FlushAsync();
        });

        var folder = Path.Combine(Path.GetTempPath(), "kojima-update-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "payload.bin");
        long last = 0;
        try
        {
            await UpdateService.DownloadAsync(
                new UpdateAsset("payload.bin", $"http://127.0.0.1:{port}/payload.bin", payload.Length),
                path,
                bytes: new SyncProgress<long>(value => last = Math.Max(last, value)));
            await server;
            Assert.Equal(payload.Length, last);
            Assert.Equal(payload.Length, new FileInfo(path).Length);
        }
        finally
        {
            listener.Stop();
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
