using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xperience.Core.Domain.Models;
using Xperience.Core.Updates;
using Xunit;

namespace Xperience.Core.Tests.Updates;

public sealed class GitHubAppUpdateServiceTests : IDisposable
{
    private readonly string _tempUpdatesDir;

    public GitHubAppUpdateServiceTests()
    {
        _tempUpdatesDir = Path.Combine(Path.GetTempPath(), $"xperience_update_svc_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempUpdatesDir);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_SendsRequiredHeadersAndResolvesAssetDigest()
    {
        string payloadHash = Convert.ToHexString(SHA256.HashData("installer-v1.2.0"u8.ToArray())).ToLowerInvariant();
        HttpRequestMessage? capturedApiRequest = null;

        string releaseJson = JsonSerializer.Serialize(new
        {
            tag_name = "v1.2.0",
            name = "Xperience v1.2.0",
            body = "## What's New\n- Faster audio DSP\n- Bug fixes",
            html_url = "https://github.com/AithonML/Xperience-Player-Windows-/releases/tag/v1.2.0",
            draft = false,
            prerelease = false,
            assets = new[]
            {
                new
                {
                    name = "XperiencePlayer-v1.2.0-win-arm64-Setup.exe",
                    browser_download_url = "https://github.com/AithonML/Xperience-Player-Windows-/releases/download/v1.2.0/XperiencePlayer-v1.2.0-win-arm64-Setup.exe",
                    size = 16L,
                    digest = $"sha256:{new string('b', 64)}"
                },
                new
                {
                    name = "XperiencePlayer-v1.2.0-win-x64-Setup.exe",
                    browser_download_url = "https://github.com/AithonML/Xperience-Player-Windows-/releases/download/v1.2.0/XperiencePlayer-v1.2.0-win-x64-Setup.exe",
                    size = 16L,
                    digest = $"sha256:{payloadHash}"
                }
            }
        });

        using var httpClient = new HttpClient(new StubHttpMessageHandler(req =>
        {
            capturedApiRequest = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(releaseJson, Encoding.UTF8, "application/json")
            };
        }));

        using var service = new GitHubAppUpdateService(
            currentVersion: new SemVersion(1, 0, 0),
            httpClient: httpClient,
            updatesDirectory: _tempUpdatesDir,
            architectureOverride: Architecture.X64);

        var result = await service.CheckForUpdatesAsync();

        Assert.NotNull(capturedApiRequest);
        Assert.Equal(GitHubAppUpdateService.DefaultLatestReleaseEndpoint, capturedApiRequest!.RequestUri!.AbsoluteUri);
        Assert.Contains("XperienceMusicPlayer-Windows/1.0.0", capturedApiRequest.Headers.UserAgent.ToString());
        Assert.Contains(capturedApiRequest.Headers.Accept, h => h.MediaType == "application/vnd.github+json");

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.NotNull(result.AvailableUpdate);
        Assert.Equal("v1.2.0", result.AvailableUpdate!.TagName);
        Assert.Equal("XperiencePlayer-v1.2.0-win-x64-Setup.exe", result.AvailableUpdate.SelectedAsset.Name);
        Assert.Equal(payloadHash, result.AvailableUpdate.ExpectedSha256);
        Assert.Contains("Faster audio DSP", result.AvailableUpdate.ReleaseNotes);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_FallsBackToChecksumsTxtWhenDigestMissing()
    {
        string expectedHash = Convert.ToHexString(SHA256.HashData("binary-data"u8.ToArray())).ToLowerInvariant();
        const string checksumUrl = "https://github.com/AithonML/Xperience-Player-Windows-/releases/download/v1.1.0/checksums.txt";

        string releaseJson = JsonSerializer.Serialize(new
        {
            tag_name = "v1.1.0",
            body = "Release notes",
            draft = false,
            assets = new object[]
            {
                new
                {
                    name = "XperiencePlayer-v1.1.0-x64-Setup.exe",
                    browser_download_url = "https://github.com/AithonML/Xperience-Player-Windows-/releases/download/v1.1.0/XperiencePlayer-v1.1.0-x64-Setup.exe",
                    size = 11L,
                    digest = (string?)null
                },
                new
                {
                    name = "checksums.txt",
                    browser_download_url = checksumUrl,
                    size = 200L,
                    digest = (string?)null
                }
            }
        });

        using var httpClient = new HttpClient(new StubHttpMessageHandler(req =>
        {
            if (req.RequestUri!.AbsoluteUri == checksumUrl)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"{expectedHash}  XperiencePlayer-v1.1.0-x64-Setup.exe\n", Encoding.UTF8)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(releaseJson, Encoding.UTF8, "application/json")
            };
        }));

        using var service = new GitHubAppUpdateService(
            currentVersion: new SemVersion(1, 0, 0),
            httpClient: httpClient,
            updatesDirectory: _tempUpdatesDir,
            architectureOverride: Architecture.X64);

        var result = await service.CheckForUpdatesAsync();

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal(expectedHash, result.AvailableUpdate!.ExpectedSha256);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_MissingChecksum_FailsClosed()
    {
        string releaseJson = JsonSerializer.Serialize(new
        {
            tag_name = "v1.1.0",
            draft = false,
            assets = new[]
            {
                new
                {
                    name = "XperiencePlayer-v1.1.0-x64-Setup.exe",
                    browser_download_url = "https://github.com/AithonML/Xperience-Player-Windows-/releases/download/v1.1.0/XperiencePlayer-v1.1.0-x64-Setup.exe",
                    size = 100L
                }
            }
        });

        using var httpClient = new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(releaseJson, Encoding.UTF8, "application/json")
        }));

        using var service = new GitHubAppUpdateService(
            currentVersion: new SemVersion(1, 0, 0),
            httpClient: httpClient,
            updatesDirectory: _tempUpdatesDir,
            architectureOverride: Architecture.X64);

        var result = await service.CheckForUpdatesAsync();

        Assert.Equal(UpdateCheckStatus.MissingChecksum, result.Status);
        Assert.Null(result.AvailableUpdate);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_SameVersion_ReturnsUpToDate()
    {
        string releaseJson = JsonSerializer.Serialize(new
        {
            tag_name = "v1.0.0",
            draft = false,
            assets = Array.Empty<object>()
        });

        using var httpClient = new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(releaseJson, Encoding.UTF8, "application/json")
        }));

        using var service = new GitHubAppUpdateService(
            currentVersion: new SemVersion(1, 0, 0),
            httpClient: httpClient,
            updatesDirectory: _tempUpdatesDir,
            architectureOverride: Architecture.X64);

        var result = await service.CheckForUpdatesAsync();

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_404NotFound_ReturnsNoPublishedReleases()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        using var service = new GitHubAppUpdateService(
            currentVersion: new SemVersion(1, 0, 0),
            httpClient: httpClient,
            updatesDirectory: _tempUpdatesDir,
            architectureOverride: Architecture.X64);

        var result = await service.CheckForUpdatesAsync();

        Assert.Equal(UpdateCheckStatus.NoPublishedReleases, result.Status);
    }

    [Fact]
    public async Task DownloadAndVerifyUpdateAsync_ValidPayload_ReportsProgressCleansStaleAndPromotesFile()
    {
        // Seed a stale file that must be cleaned before downloading
        string staleFile = Path.Combine(_tempUpdatesDir, "old-installer.exe");
        await File.WriteAllBytesAsync(staleFile, "stale"u8.ToArray());

        byte[] payload = new byte[250_000];
        Random.Shared.NextBytes(payload);
        string expectedHash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

        using var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
        {
            var content = new ByteArrayContent(payload);
            content.Headers.ContentLength = payload.Length;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }));

        using var service = new GitHubAppUpdateService(
            currentVersion: new SemVersion(1, 0, 0),
            httpClient: httpClient,
            updatesDirectory: _tempUpdatesDir,
            architectureOverride: Architecture.X64);

        var release = new UpdateReleaseInfo(
            Version: new SemVersion(1, 1, 0),
            TagName: "v1.1.0",
            ReleaseName: "v1.1.0",
            ReleaseNotes: "Notes",
            ReleaseUrl: "https://github.com/AithonML/Xperience-Player-Windows-/releases/tag/v1.1.0",
            PublishedAt: DateTimeOffset.UtcNow,
            SelectedAsset: new GitHubAssetDto
            {
                Name = "XperiencePlayer-v1.1.0-x64-Setup.exe",
                BrowserDownloadUrl = "https://github.com/AithonML/Xperience-Player-Windows-/releases/download/v1.1.0/XperiencePlayer-v1.1.0-x64-Setup.exe",
                Size = payload.Length
            },
            ExpectedSha256: expectedHash);

        var progressReports = new List<UpdateDownloadProgress>();
        var syncProgress = new SynchronousProgress<UpdateDownloadProgress>(p => progressReports.Add(p));

        var downloadResult = await service.DownloadAndVerifyUpdateAsync(release, syncProgress);

        Assert.True(downloadResult.Succeeded);
        Assert.False(File.Exists(staleFile));
        Assert.NotNull(downloadResult.VerifiedFilePath);
        Assert.True(File.Exists(downloadResult.VerifiedFilePath));
        Assert.Equal(expectedHash, downloadResult.ComputedSha256);
        Assert.DoesNotContain(Directory.EnumerateFiles(_tempUpdatesDir), f => f.EndsWith(".part", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(progressReports, p => p.Phase == UpdateDownloadPhase.Downloading);
        Assert.Contains(progressReports, p => p.Phase == UpdateDownloadPhase.VerifyingIntegrity);
        Assert.Contains(progressReports, p => p.Phase == UpdateDownloadPhase.ReadyToInstall && p.ProgressPercent == 100.0);
    }

    [Fact]
    public async Task DownloadAndVerifyUpdateAsync_HashMismatch_ImmediatelyDeletesTempFileAndNeverPromotes()
    {
        byte[] tamperedPayload = "tampered-executable-bytes"u8.ToArray();
        string expectedHash = new string('f', 64);

        using var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
        {
            var content = new ByteArrayContent(tamperedPayload);
            content.Headers.ContentLength = tamperedPayload.Length;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }));

        using var service = new GitHubAppUpdateService(
            currentVersion: new SemVersion(1, 0, 0),
            httpClient: httpClient,
            updatesDirectory: _tempUpdatesDir,
            architectureOverride: Architecture.X64);

        var release = new UpdateReleaseInfo(
            Version: new SemVersion(1, 1, 0),
            TagName: "v1.1.0",
            ReleaseName: "v1.1.0",
            ReleaseNotes: "Notes",
            ReleaseUrl: "https://github.com/AithonML/Xperience-Player-Windows-/releases/tag/v1.1.0",
            PublishedAt: DateTimeOffset.UtcNow,
            SelectedAsset: new GitHubAssetDto
            {
                Name = "XperiencePlayer-v1.1.0-x64-Setup.exe",
                BrowserDownloadUrl = "https://github.com/AithonML/Xperience-Player-Windows-/releases/download/v1.1.0/XperiencePlayer-v1.1.0-x64-Setup.exe",
                Size = tamperedPayload.Length
            },
            ExpectedSha256: expectedHash);

        var downloadResult = await service.DownloadAndVerifyUpdateAsync(release);

        Assert.False(downloadResult.Succeeded);
        Assert.True(downloadResult.IsIntegrityMismatch);
        Assert.Null(downloadResult.VerifiedFilePath);
        Assert.Empty(Directory.EnumerateFiles(_tempUpdatesDir));
    }

    [Theory]
    [InlineData("../escape.exe")]
    [InlineData("..\\escape.exe")]
    [InlineData("sub/escape.exe")]
    [InlineData("installer.bat")]
    public void TryBuildSafeDestinationPaths_RejectsPathTraversalAndUnsupportedExtensions(string maliciousName)
    {
        using var service = new GitHubAppUpdateService(
            currentVersion: new SemVersion(1, 0, 0),
            updatesDirectory: _tempUpdatesDir);

        bool allowed = service.TryBuildSafeDestinationPaths(maliciousName, out _, out _);

        Assert.False(allowed);
    }

    [Fact]
    public void UpdateSettings_EmptyJsonDeserialization_DefaultsAutoCheckToTrue()
    {
        var deserialized = JsonSerializer.Deserialize<UpdateSettings>("{}");

        Assert.NotNull(deserialized);
        Assert.True(deserialized!.AutoCheckUpdatesOnStartup);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_Utf8BomPrefixedChecksumsTxtBytes_ParsesExpectedSha256()
    {
        string expectedHash = Convert.ToHexString(SHA256.HashData("bom-payload"u8.ToArray())).ToLowerInvariant();
        const string checksumUrl = "https://github.com/AithonML/Xperience-Player-Windows-/releases/download/v1.1.0/checksums.txt";

        string releaseJson = JsonSerializer.Serialize(new
        {
            tag_name = "v1.1.0",
            body = "Release notes",
            draft = false,
            assets = new object[]
            {
                new
                {
                    name = "XperiencePlayer-v1.1.0-x64-Setup.exe",
                    browser_download_url = "https://github.com/AithonML/Xperience-Player-Windows-/releases/download/v1.1.0/XperiencePlayer-v1.1.0-x64-Setup.exe",
                    size = 11L,
                    digest = (string?)null
                },
                new
                {
                    name = "checksums.txt",
                    browser_download_url = checksumUrl,
                    size = 200L,
                    digest = (string?)null
                }
            }
        });

        // Prepend literal UTF-8 BOM bytes (0xEF 0xBB 0xBF) as emitted by Windows tools
        byte[] bomPrefix = [0xEF, 0xBB, 0xBF];
        byte[] textBytes = Encoding.UTF8.GetBytes($"{expectedHash}  XperiencePlayer-v1.1.0-x64-Setup.exe\r\n");
        byte[] bomPayload = [.. bomPrefix, .. textBytes];

        using var httpClient = new HttpClient(new StubHttpMessageHandler(req =>
        {
            if (req.RequestUri!.AbsoluteUri == checksumUrl)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(bomPayload)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(releaseJson, Encoding.UTF8, "application/json")
            };
        }));

        using var service = new GitHubAppUpdateService(
            currentVersion: new SemVersion(1, 0, 0),
            httpClient: httpClient,
            updatesDirectory: _tempUpdatesDir,
            architectureOverride: Architecture.X64);

        var result = await service.CheckForUpdatesAsync();

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal(expectedHash, result.AvailableUpdate!.ExpectedSha256);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_StalledApiRequest_TimesOutCleanlyWithNetworkError()
    {
        using var httpClient = new HttpClient(new AsyncStubHttpMessageHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }))
        {
            Timeout = TimeSpan.FromMinutes(15)
        };

        using var service = new GitHubAppUpdateService(
            currentVersion: new SemVersion(1, 0, 0),
            httpClient: httpClient,
            updatesDirectory: _tempUpdatesDir,
            architectureOverride: Architecture.X64,
            metadataTimeout: TimeSpan.FromMilliseconds(80));

        var result = await service.CheckForUpdatesAsync();

        Assert.Equal(UpdateCheckStatus.NetworkError, result.Status);
        Assert.Contains("timed out", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_StalledChecksumAsset_TimesOutCleanlyWithNetworkError()
    {
        const string checksumUrl = "https://github.com/AithonML/Xperience-Player-Windows-/releases/download/v1.1.0/checksums.txt";
        string releaseJson = JsonSerializer.Serialize(new
        {
            tag_name = "v1.1.0",
            draft = false,
            assets = new object[]
            {
                new
                {
                    name = "XperiencePlayer-v1.1.0-x64-Setup.exe",
                    browser_download_url = "https://github.com/AithonML/Xperience-Player-Windows-/releases/download/v1.1.0/XperiencePlayer-v1.1.0-x64-Setup.exe",
                    size = 100L,
                    digest = (string?)null
                },
                new
                {
                    name = "checksums.txt",
                    browser_download_url = checksumUrl,
                    size = 200L,
                    digest = (string?)null
                }
            }
        });

        using var httpClient = new HttpClient(new AsyncStubHttpMessageHandler(async (req, ct) =>
        {
            if (req.RequestUri!.AbsoluteUri == checksumUrl)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(releaseJson, Encoding.UTF8, "application/json")
            };
        }))
        {
            Timeout = TimeSpan.FromMinutes(15)
        };

        using var service = new GitHubAppUpdateService(
            currentVersion: new SemVersion(1, 0, 0),
            httpClient: httpClient,
            updatesDirectory: _tempUpdatesDir,
            architectureOverride: Architecture.X64,
            metadataTimeout: TimeSpan.FromMilliseconds(80));

        var result = await service.CheckForUpdatesAsync();

        Assert.Equal(UpdateCheckStatus.NetworkError, result.Status);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempUpdatesDir))
            {
                Directory.Delete(_tempUpdatesDir, recursive: true);
            }
        }
        catch { }
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = responder(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }

    private sealed class AsyncStubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await responder(request, cancellationToken);
            response.RequestMessage ??= request;
            return response;
        }
    }

    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
