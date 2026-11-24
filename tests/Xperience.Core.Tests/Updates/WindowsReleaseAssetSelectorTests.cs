using System.Runtime.InteropServices;
using Xperience.Core.Domain.Models;
using Xperience.Core.Updates;
using Xunit;

namespace Xperience.Core.Tests.Updates;

public sealed class WindowsReleaseAssetSelectorTests
{
    private static GitHubAssetDto Asset(string name, string? url = null) => new()
    {
        Name = name,
        BrowserDownloadUrl = url ?? $"https://github.com/AithonML/Xperience-Player-Windows-/releases/download/v1.1.0/{name}",
        Size = 42_000_000
    };

    [Fact]
    public void SelectInstallerAsset_UnpackagedX64_PrefersMatchingX64Exe()
    {
        var assets = new[]
        {
            Asset("checksums.txt"),
            Asset("XperiencePlayer-v1.1.0-win-arm64-Setup.exe"),
            Asset("XperiencePlayer-v1.1.0-win-x64.msix"),
            Asset("XperiencePlayer-v1.1.0-win-x64-Setup.exe"),
            Asset("XperiencePlayer-v1.1.0-win-x64-portable.zip")
        };

        var selected = WindowsReleaseAssetSelector.SelectInstallerAsset(assets, Architecture.X64, isPackagedApp: false);

        Assert.NotNull(selected);
        Assert.Equal("XperiencePlayer-v1.1.0-win-x64-Setup.exe", selected!.Name);
    }

    [Fact]
    public void SelectInstallerAsset_UnpackagedArm64_PrefersMatchingArm64Exe()
    {
        var assets = new[]
        {
            Asset("XperiencePlayer-v1.1.0-win-x64-Setup.exe"),
            Asset("XperiencePlayer-v1.1.0-win-arm64-Setup.exe"),
            Asset("XperiencePlayer-v1.1.0.msixbundle")
        };

        var selected = WindowsReleaseAssetSelector.SelectInstallerAsset(assets, Architecture.Arm64, isPackagedApp: false);

        Assert.NotNull(selected);
        Assert.Equal("XperiencePlayer-v1.1.0-win-arm64-Setup.exe", selected!.Name);
    }

    [Fact]
    public void SelectInstallerAsset_X8664Token_MatchesX64AndNotX86()
    {
        var assets = new[]
        {
            Asset("XperiencePlayer-v1.1.0-windows-x86_64.exe")
        };

        var x64Match = WindowsReleaseAssetSelector.SelectInstallerAsset(assets, Architecture.X64, isPackagedApp: false);
        var x86Match = WindowsReleaseAssetSelector.SelectInstallerAsset(assets, Architecture.X86, isPackagedApp: false);

        Assert.NotNull(x64Match);
        Assert.Null(x86Match);
    }

    [Fact]
    public void SelectInstallerAsset_OnlyOpposingArchitecture_ReturnsNull()
    {
        var assets = new[]
        {
            Asset("XperiencePlayer-v1.1.0-arm64-Setup.exe"),
            Asset("XperiencePlayer-v1.1.0-arm64.msix")
        };

        var selected = WindowsReleaseAssetSelector.SelectInstallerAsset(assets, Architecture.X64, isPackagedApp: false);

        Assert.Null(selected);
    }

    [Fact]
    public void SelectInstallerAsset_Unpackaged_FallsBackToMsixBundleWhenNoExeExists()
    {
        var assets = new[]
        {
            Asset("XperiencePlayer-v1.1.0.msixbundle"),
            Asset("checksums.txt")
        };

        var selected = WindowsReleaseAssetSelector.SelectInstallerAsset(assets, Architecture.X64, isPackagedApp: false);

        Assert.NotNull(selected);
        Assert.Equal("XperiencePlayer-v1.1.0.msixbundle", selected!.Name);
    }

    [Fact]
    public void SelectInstallerAsset_Packaged_PrefersMatchingMsixOverExe()
    {
        var assets = new[]
        {
            Asset("XperiencePlayer-v1.1.0-x64-Setup.exe"),
            Asset("XperiencePlayer-v1.1.0-x64.msix")
        };

        var selected = WindowsReleaseAssetSelector.SelectInstallerAsset(assets, Architecture.X64, isPackagedApp: true);

        Assert.NotNull(selected);
        Assert.Equal("XperiencePlayer-v1.1.0-x64.msix", selected!.Name);
    }

    [Fact]
    public void SelectInstallerAsset_RejectsNonHttpsDownloadUrls()
    {
        var assets = new[]
        {
            Asset("XperiencePlayer-v1.1.0-x64-Setup.exe", "http://example.com/setup.exe"),
            Asset("XperiencePlayer-v1.1.0-x64.msix", "file:///C:/setup.msix")
        };

        var selected = WindowsReleaseAssetSelector.SelectInstallerAsset(assets, Architecture.X64, isPackagedApp: false);

        Assert.Null(selected);
    }
}
