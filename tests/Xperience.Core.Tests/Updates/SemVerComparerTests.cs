using Xperience.Core.Domain.Models;
using Xperience.Core.Updates;
using Xunit;

namespace Xperience.Core.Tests.Updates;

public sealed class SemVerComparerTests
{
    [Theory]
    [InlineData("v1.0.1", 1, 0, 1, null)]
    [InlineData("V2.14.3", 2, 14, 3, null)]
    [InlineData("1.0.0", 1, 0, 0, null)]
    [InlineData("1.2", 1, 2, 0, null)]
    [InlineData("1.0.0.0", 1, 0, 0, null)]
    [InlineData("v1.0.0-alpha.1+exp.sha.5114f85", 1, 0, 0, "alpha.1")]
    [InlineData("  v3.4.5-rc.2  ", 3, 4, 5, "rc.2")]
    public void TryParse_ValidVersions_ParsesExpectedComponents(
        string raw,
        int expectedMajor,
        int expectedMinor,
        int expectedPatch,
        string? expectedPreRelease)
    {
        bool parsed = SemVerComparer.TryParse(raw, out var version);

        Assert.True(parsed);
        Assert.NotNull(version);
        Assert.Equal(expectedMajor, version!.Major);
        Assert.Equal(expectedMinor, version.Minor);
        Assert.Equal(expectedPatch, version.Patch);
        Assert.Equal(expectedPreRelease, version.PreRelease);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("v")]
    [InlineData("1")]
    [InlineData("01.0.0")]
    [InlineData("1.02.0")]
    [InlineData("1.0.0-01")]
    [InlineData("1.0.0-")]
    [InlineData("1.0.0+")]
    [InlineData("1.0.0..1")]
    [InlineData("1.0.0.5")]
    [InlineData("not-a-version")]
    public void TryParse_InvalidVersions_ReturnsFalse(string? raw)
    {
        bool parsed = SemVerComparer.TryParse(raw, out var version);

        Assert.False(parsed);
        Assert.Null(version);
    }

    [Fact]
    public void Compare_SemVer200OfficialPrecedenceChain_IsStrictlyOrdered()
    {
        string[] orderedTags =
        [
            "v1.0.0-alpha",
            "v1.0.0-alpha.1",
            "v1.0.0-alpha.beta",
            "v1.0.0-beta",
            "v1.0.0-beta.2",
            "v1.0.0-beta.11",
            "v1.0.0-rc.1",
            "v1.0.0",
            "v1.0.1",
            "v1.1.0",
            "v1.10.0",
            "v2.0.0-alpha.1",
            "v2.0.0"
        ];

        var versions = orderedTags.Select(SemVerComparer.Parse).ToArray();

        for (int i = 0; i < versions.Length; i++)
        {
            Assert.Equal(0, SemVerComparer.Compare(versions[i], versions[i]));
            for (int j = i + 1; j < versions.Length; j++)
            {
                Assert.True(SemVerComparer.Compare(versions[i], versions[j]) < 0, $"{versions[i]} should be < {versions[j]}");
                Assert.True(SemVerComparer.Compare(versions[j], versions[i]) > 0, $"{versions[j]} should be > {versions[i]}");
                Assert.True(SemVerComparer.IsNewer(versions[j], versions[i]));
                Assert.False(SemVerComparer.IsNewer(versions[i], versions[j]));
            }
        }
    }

    [Fact]
    public void Compare_NumericPreReleaseIdentifier_HasLowerPrecedenceThanAlphaNumeric()
    {
        var numeric = SemVerComparer.Parse("1.0.0-1");
        var alpha = SemVerComparer.Parse("1.0.0-alpha");

        Assert.True(numeric < alpha);
        Assert.True(SemVerComparer.IsNewer(alpha, numeric));
    }

    [Fact]
    public void Compare_BuildMetadataIsIgnoredForPrecedence()
    {
        var left = SemVerComparer.Parse("v1.2.3+build.1");
        var right = SemVerComparer.Parse("1.2.3+20261122");

        Assert.Equal(0, SemVerComparer.Compare(left, right));
        Assert.False(SemVerComparer.IsNewer(left, right));
        Assert.False(SemVerComparer.IsNewer(right, left));
        Assert.Equal(left, right);
    }
}
