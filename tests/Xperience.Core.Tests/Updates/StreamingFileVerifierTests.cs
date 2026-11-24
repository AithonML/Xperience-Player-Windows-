using System.Security.Cryptography;
using Xperience.Core.Updates;
using Xunit;

namespace Xperience.Core.Tests.Updates;

public sealed class StreamingFileVerifierTests : IDisposable
{
    private readonly string _tempDir;

    public StreamingFileVerifierTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"xperience_verifier_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task VerifyAndDeleteOnMismatchAsync_MatchingMultiChunkFile_ReturnsTrueAndPreservesFile()
    {
        string filePath = Path.Combine(_tempDir, "installer.part");
        // > 256 KB to span multiple 80 KB streaming read chunks
        byte[] payload = new byte[300_000];
        Random.Shared.NextBytes(payload);
        await File.WriteAllBytesAsync(filePath, payload);

        string expectedHex = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

        var (isValid, computed) = await StreamingFileVerifier.VerifyAndDeleteOnMismatchAsync(
            filePath,
            $"sha256:{expectedHex.ToUpperInvariant()}");

        Assert.True(isValid);
        Assert.Equal(expectedHex, computed);
        Assert.True(File.Exists(filePath));
    }

    [Fact]
    public async Task VerifyAndDeleteOnMismatchAsync_HashMismatch_ImmediatelyDeletesFile()
    {
        string filePath = Path.Combine(_tempDir, "tampered.part");
        await File.WriteAllBytesAsync(filePath, "corrupted installer payload"u8.ToArray());

        string wrongHash = new string('a', 64);

        var (isValid, computed) = await StreamingFileVerifier.VerifyAndDeleteOnMismatchAsync(filePath, wrongHash);

        Assert.False(isValid);
        Assert.NotNull(computed);
        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public async Task VerifyAndDeleteOnMismatchAsync_MissingOrMalformedExpectedHash_FailsClosedAndDeletesFile()
    {
        string filePath = Path.Combine(_tempDir, "unverified.part");
        await File.WriteAllBytesAsync(filePath, "some bytes"u8.ToArray());

        var (isValid, computed) = await StreamingFileVerifier.VerifyAndDeleteOnMismatchAsync(filePath, "not-a-hash");

        Assert.False(isValid);
        Assert.Null(computed);
        Assert.False(File.Exists(filePath));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch { }
    }
}
