using Xperience.Core.Domain.Models;
using Xperience.Core.Updates;
using Xunit;

namespace Xperience.Core.Tests.Updates;

public sealed class Sha256ChecksumParserTests
{
    private const string ValidHashLower = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    private const string ValidHashUpper = "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855";
    private const string OtherValidHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("sha256:" + ValidHashLower)]
    [InlineData("SHA256:" + ValidHashUpper)]
    [InlineData("sha-256: " + ValidHashLower)]
    [InlineData(ValidHashLower)]
    [InlineData("  " + ValidHashUpper + "  ")]
    public void TryParseAssetDigest_ValidFormats_NormalizesToLowercase64Hex(string digest)
    {
        bool ok = Sha256ChecksumParser.TryParseAssetDigest(digest, out string normalized);

        Assert.True(ok);
        Assert.Equal(ValidHashLower, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha512:" + ValidHashLower)]
    [InlineData("md5:d41d8cd98f00b204e9800998ecf8427e")]
    [InlineData("sha256:short")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void TryParseAssetDigest_InvalidOrUnsupportedAlgorithm_FailsClosed(string? digest)
    {
        bool ok = Sha256ChecksumParser.TryParseAssetDigest(digest, out string normalized);

        Assert.False(ok);
        Assert.Equal(string.Empty, normalized);
    }

    [Fact]
    public void TryExtractHashFromChecksumText_GnuFormat_MatchesExactAssetName()
    {
        string manifest = $"""
            # Official release checksums
            {OtherValidHash}  XperiencePlayer-arm64-Setup.exe
            {ValidHashUpper} *./dist/XperiencePlayer-x64-Setup.exe
            """;

        bool ok = Sha256ChecksumParser.TryExtractHashFromChecksumText(
            manifest,
            "XperiencePlayer-x64-Setup.exe",
            isAssetSpecificFile: false,
            out string normalized);

        Assert.True(ok);
        Assert.Equal(ValidHashLower, normalized);
    }

    [Fact]
    public void TryExtractHashFromChecksumText_BsdFormat_MatchesExactAssetName()
    {
        string manifest = $"SHA256 (XperiencePlayer-x64-Setup.exe) = {ValidHashUpper}";

        bool ok = Sha256ChecksumParser.TryExtractHashFromChecksumText(
            manifest,
            "XperiencePlayer-x64-Setup.exe",
            isAssetSpecificFile: false,
            out string normalized);

        Assert.True(ok);
        Assert.Equal(ValidHashLower, normalized);
    }

    [Fact]
    public void TryExtractHashFromChecksumText_BareHashLine_AcceptedOnlyForAssetSpecificFile()
    {
        bool acceptedSpecific = Sha256ChecksumParser.TryExtractHashFromChecksumText(
            ValidHashUpper,
            "XperiencePlayer-x64-Setup.exe",
            isAssetSpecificFile: true,
            out string specificHash);

        bool acceptedManifest = Sha256ChecksumParser.TryExtractHashFromChecksumText(
            ValidHashUpper,
            "XperiencePlayer-x64-Setup.exe",
            isAssetSpecificFile: false,
            out _);

        Assert.True(acceptedSpecific);
        Assert.Equal(ValidHashLower, specificHash);
        Assert.False(acceptedManifest);
    }

    [Fact]
    public void FindChecksumAssets_PrioritizesPerAssetFileOverChecksumsTxtAndFiltersHttp()
    {
        var assets = new[]
        {
            new GitHubAssetDto
            {
                Name = "checksums.txt",
                BrowserDownloadUrl = "https://github.com/org/repo/releases/download/v1.1.0/checksums.txt"
            },
            new GitHubAssetDto
            {
                Name = "XperiencePlayer-x64-Setup.exe.sha256",
                BrowserDownloadUrl = "https://github.com/org/repo/releases/download/v1.1.0/XperiencePlayer-x64-Setup.exe.sha256"
            },
            new GitHubAssetDto
            {
                Name = "SHA256SUMS",
                BrowserDownloadUrl = "http://insecure.example.com/SHA256SUMS"
            }
        };

        var matches = Sha256ChecksumParser.FindChecksumAssets(assets, "XperiencePlayer-x64-Setup.exe");

        Assert.Equal(2, matches.Count);
        Assert.Equal("XperiencePlayer-x64-Setup.exe.sha256", matches[0].Asset.Name);
        Assert.True(matches[0].IsAssetSpecific);
        Assert.Equal("checksums.txt", matches[1].Asset.Name);
        Assert.False(matches[1].IsAssetSpecific);
    }

    [Fact]
    public void TryExtractHashFromChecksumText_Utf8BomPrefixedLines_ParsesCleanly()
    {
        string bomManifest = $"\uFEFF{ValidHashUpper}  XperiencePlayer-x64-Setup.exe\r\n{OtherValidHash}  other.exe";
        string bomBareFile = $"\uFEFF{ValidHashLower}\r\n";

        bool manifestOk = Sha256ChecksumParser.TryExtractHashFromChecksumText(
            bomManifest,
            "XperiencePlayer-x64-Setup.exe",
            isAssetSpecificFile: false,
            out string manifestHash);

        bool bareOk = Sha256ChecksumParser.TryExtractHashFromChecksumText(
            bomBareFile,
            "XperiencePlayer-x64-Setup.exe",
            isAssetSpecificFile: true,
            out string bareHash);

        bool digestOk = Sha256ChecksumParser.TryNormalizeSha256($"\uFEFFsha256:{ValidHashUpper}", out string digestHash);

        Assert.True(manifestOk);
        Assert.Equal(ValidHashLower, manifestHash);
        Assert.True(bareOk);
        Assert.Equal(ValidHashLower, bareHash);
        Assert.True(digestOk);
        Assert.Equal(ValidHashLower, digestHash);
    }
}
