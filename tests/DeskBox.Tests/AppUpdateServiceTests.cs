using System.Net;
using System.Security.Cryptography;
using System.Text;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class AppUpdateServiceTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("1.2.1", "1.2.2", true)]
    [InlineData("1.2.1", "v1.2.2", true)]
    [InlineData("1.2.1", "1.2.2-beta.1", true)]
    [InlineData("1.2.1", "1.2.1", false)]
    [InlineData("1.2.1", "1.2.0", false)]
    [InlineData("1.2.1", "not-a-version", false)]
    public void IsRemoteVersionNewer_ComparesSemanticVersionPrefix(string currentVersion, string remoteVersion, bool expected)
    {
        Assert.Equal(expected, AppUpdateService.IsRemoteVersionNewer(currentVersion, remoteVersion));
    }

    [Fact]
    public async Task CheckForUpdatesAsync_ReadsManifestAndDetectsUpdate()
    {
        using var httpClient = CreateHttpClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {
                  "schemaVersion": 1,
                  "version": "1.2.2",
                  "downloadUrl": "https://github.com/Tianyu199509/DeskBox/releases/download/v1.2.2/DeskBox_Setup_1.2.2_x64.exe",
                  "manualDownloadUrl": "https://pan.quark.cn/s/version-specific",
                  "sha256": "abc",
                  "summary": {
                    "zh-CN": "修复多屏定位"
                  }
                }
                """,
                Encoding.UTF8,
                "application/json")
        });

        var service = new AppUpdateService(httpClient, "https://deskbox.fun/update/stable.json", _tempRoot);

        var result = await service.CheckForUpdatesAsync("1.2.1");

        Assert.Equal(AppUpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.NotNull(result.Manifest);
        Assert.Equal("1.2.2", result.Manifest.Version);
        Assert.Equal("https://pan.quark.cn/s/version-specific", result.Manifest.ManualDownloadUrl);
        Assert.Same(result, service.LastCheckResult);
    }

    [Fact]
    public async Task DownloadUpdateAsync_RejectsInstallerWithoutHash()
    {
        using var httpClient = CreateHttpClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3])
        });
        var service = new AppUpdateService(httpClient, updateRootPath: _tempRoot);

        var result = await service.DownloadUpdateAsync(new AppUpdateManifest
        {
            Version = "1.2.2",
            DownloadUrl = "https://example.com/DeskBox_Setup_1.2.2_x64.exe"
        });

        Assert.False(result.Success);
        Assert.Equal(AppUpdateDownloadFailureKind.HashMissing, result.FailureKind);
    }

    [Fact]
    public async Task DownloadUpdateAsync_VerifiesSha256AndWritesInstaller()
    {
        byte[] payload = Encoding.UTF8.GetBytes("deskbox-installer");
        string sha256 = Convert.ToHexString(SHA256.HashData(payload));
        using var httpClient = CreateHttpClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload)
        });
        var service = new AppUpdateService(httpClient, updateRootPath: _tempRoot);

        var result = await service.DownloadUpdateAsync(new AppUpdateManifest
        {
            Version = "1.2.2",
            DownloadUrl = "https://example.com/DeskBox_Setup_1.2.2_x64.exe",
            Sha256 = sha256,
            Size = payload.Length
        });

        Assert.True(result.Success, $"{result.FailureKind}: {result.ErrorMessage}");
        Assert.True(File.Exists(result.FilePath));
        Assert.Equal(payload, await File.ReadAllBytesAsync(result.FilePath!));
    }

    [Fact]
    public async Task PrepareDetachedUpdaterHelper_CopiesUpdaterOutsideAppDirectory()
    {
        string appDirectory = Path.Combine(_tempRoot, "app");
        string updateRoot = Path.Combine(_tempRoot, "updates");
        Directory.CreateDirectory(appDirectory);

        string sourceExe = Path.Combine(appDirectory, "DeskBox.Updater.exe");
        string sourceRuntimeConfig = Path.Combine(appDirectory, "DeskBox.Updater.runtimeconfig.json");
        await File.WriteAllTextAsync(sourceExe, "exe");
        await File.WriteAllTextAsync(sourceRuntimeConfig, "{}");

        string detachedExe = AppUpdateService.PrepareDetachedUpdaterHelper(appDirectory, updateRoot);

        Assert.True(File.Exists(detachedExe));
        Assert.NotEqual(sourceExe, detachedExe);
        Assert.StartsWith(Path.Combine(updateRoot, "helper"), detachedExe, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("exe", await File.ReadAllTextAsync(detachedExe));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(detachedExe)!, "DeskBox.Updater.runtimeconfig.json")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private static HttpClient CreateHttpClient(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        return new HttpClient(new StubHttpMessageHandler(handler));
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }
}
