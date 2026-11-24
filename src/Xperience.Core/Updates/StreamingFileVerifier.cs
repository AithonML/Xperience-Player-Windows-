using System.Buffers;
using System.Security.Cryptography;

namespace Xperience.Core.Updates;

/// <summary>
/// Performs streaming SHA-256 verification of downloaded installer files without full-file RAM buffering,
/// enforcing constant-time digest comparison and immediate file deletion on any mismatch or error.
/// </summary>
public static class StreamingFileVerifier
{
    private const int StreamingBufferSize = 81920;

    /// <summary>
    /// Computes the lowercase hex SHA-256 hash of <paramref name="filePath"/> using streaming buffered reads.
    /// </summary>
    public static async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(StreamingBufferSize);
        try
        {
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: StreamingBufferSize,
                useAsync: true);

            while (true)
            {
                int bytesRead = await stream.ReadAsync(buffer.AsMemory(0, StreamingBufferSize), cancellationToken)
                    .ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    break;
                }

                hasher.AppendData(buffer, 0, bytesRead);
            }

            byte[] hash = hasher.GetCurrentHash();
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Verifies <paramref name="filePath"/> against <paramref name="expectedSha256"/> in streaming fashion.
    /// If the expected hash is missing/malformed or does not match the computed digest (or if verification is cancelled/fails),
    /// <paramref name="filePath"/> is IMMEDIATELY deleted from disk.
    /// </summary>
    public static async Task<(bool IsValid, string? ComputedSha256)> VerifyAndDeleteOnMismatchAsync(
        string filePath,
        string? expectedSha256,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (!Sha256ChecksumParser.TryNormalizeSha256(expectedSha256, out string normalizedExpected))
        {
            TryDeleteFile(filePath);
            return (false, null);
        }

        string computedHex;
        try
        {
            computedHex = await ComputeSha256Async(filePath, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryDeleteFile(filePath);
            throw;
        }

        byte[] expectedBytes = Convert.FromHexString(normalizedExpected);
        byte[] computedBytes = Convert.FromHexString(computedHex);

        bool matched = CryptographicOperations.FixedTimeEquals(expectedBytes, computedBytes);
        if (!matched)
        {
            TryDeleteFile(filePath);
        }

        return (matched, computedHex);
    }

    /// <summary>
    /// Best-effort immediate file deletion used for security cleanup on integrity mismatch, cancellation, or error.
    /// </summary>
    public static void TryDeleteFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch
        {
            // Ignore transient cleanup failures
        }
    }
}
