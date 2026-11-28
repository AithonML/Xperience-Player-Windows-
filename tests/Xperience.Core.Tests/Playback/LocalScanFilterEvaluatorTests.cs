using Xperience.Core.Domain.Enums;
using Xperience.Core.Domain.Models;
using Xperience.Core.Playback;
using Xunit;

namespace Xperience.Core.Tests.Playback;

public sealed class LocalScanFilterEvaluatorTests
{
    [Fact]
    public void BlacklistMode_ShallowRule_ExcludesOnlyDirectChildren_NotSubfolders()
    {
        var settings = new LocalScanFilterSettings
        {
            FilterMode = LocalScanFilterMode.Blacklist,
            GlobalDeepScan = true,
            Rules =
            [
                new FolderScanRule
                {
                    FolderPath = @"C:\Music\VoiceMemos",
                    DisplayName = "VoiceMemos",
                    ListType = FolderScanListType.Blacklist,
                    DepthMode = FolderScanDepthMode.Shallow
                }
            ]
        };

        Assert.False(LocalScanFilterEvaluator.ShouldIncludeFile(@"C:\Music\VoiceMemos\memo1.mp3", settings));
        Assert.True(LocalScanFilterEvaluator.ShouldIncludeFile(@"C:\Music\VoiceMemos\KeepSub\song.flac", settings));
        Assert.True(LocalScanFilterEvaluator.ShouldIncludeFile(@"C:\Music\Albums\track.flac", settings));
    }

    [Fact]
    public void BlacklistMode_DeepRule_ExcludesDirectChildrenAndSubfolders()
    {
        var settings = new LocalScanFilterSettings
        {
            FilterMode = LocalScanFilterMode.Blacklist,
            GlobalDeepScan = false,
            Rules =
            [
                new FolderScanRule
                {
                    FolderPath = @"C:\Music\Podcasts",
                    DisplayName = "Podcasts",
                    ListType = FolderScanListType.Blacklist,
                    DepthMode = FolderScanDepthMode.Deep
                }
            ]
        };

        Assert.False(LocalScanFilterEvaluator.ShouldIncludeFile(@"C:\Music\Podcasts\ep1.mp3", settings));
        Assert.False(LocalScanFilterEvaluator.ShouldIncludeFile(@"C:\Music\Podcasts\Season2\ep2.mp3", settings));
        Assert.True(LocalScanFilterEvaluator.ShouldIncludeFile(@"C:\Music\Albums\song.flac", settings));
    }

    [Fact]
    public void WhitelistMode_IncludesOnlyWhitelistedFolders_HonoringGlobalAndPerFolderDepth()
    {
        var settings = new LocalScanFilterSettings
        {
            FilterMode = LocalScanFilterMode.Whitelist,
            GlobalDeepScan = false, // global shallow
            Rules =
            [
                new FolderScanRule
                {
                    FolderPath = @"C:\Music\Singles",
                    DisplayName = "Singles",
                    ListType = FolderScanListType.Whitelist,
                    DepthMode = FolderScanDepthMode.InheritGlobal // inherits shallow
                },
                new FolderScanRule
                {
                    FolderPath = @"C:\Music\FLAC",
                    DisplayName = "FLAC",
                    ListType = FolderScanListType.Whitelist,
                    DepthMode = FolderScanDepthMode.Deep // overrides to deep
                }
            ]
        };

        Assert.True(LocalScanFilterEvaluator.ShouldIncludeFile(@"C:\Music\Singles\hit.mp3", settings));
        Assert.False(LocalScanFilterEvaluator.ShouldIncludeFile(@"C:\Music\Singles\Sub\nested.mp3", settings));
        Assert.True(LocalScanFilterEvaluator.ShouldIncludeFile(@"C:\Music\FLAC\Album1\track.flac", settings));
        Assert.False(LocalScanFilterEvaluator.ShouldIncludeFile(@"C:\Music\Other\song.mp3", settings));
    }
}
