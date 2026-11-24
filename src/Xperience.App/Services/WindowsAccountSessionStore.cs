using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace Xperience.App.Services;

/// <summary>
/// Persisted YouTube Music account session payload.
/// </summary>
public sealed record YouTubeAccountSessionData(
    string CookieHeader,
    string? AccountLabel,
    string? VisitorData,
    string? DataSyncId);

/// <summary>
/// DPAPI-encrypted (<c>crypt32.dll</c> <c>CryptProtectData</c> / <c>CryptUnprotectData</c>) storage for the
/// user's YouTube Music account session, with user-only NTFS ACL hardening, atomic writes, and automatic
/// one-time migration &amp; zero-overwrite deletion of legacy plaintext session files.
/// </summary>
public sealed class WindowsAccountSessionStore
{
    private const int CryptProtectUiForbidden = 0x1;

    private static readonly byte[] AppEntropy =
        Encoding.UTF8.GetBytes("XperienceMusicPlayer.Windows.YouTubeSession.DPAPI.v1");

    private readonly string _authDirectory;
    private readonly string _sessionFilePath;
    private readonly string _legacyPlaintextSessionFilePath;

    public WindowsAccountSessionStore()
    {
        string appDataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XperienceMusicPlayer");

        _authDirectory = Path.Combine(appDataRoot, "Auth");
        _sessionFilePath = Path.Combine(_authDirectory, "ytmusic_session.dat");
        _legacyPlaintextSessionFilePath = Path.Combine(appDataRoot, "OnlineCache", "ytmusic_account_session.json");
    }

    /// <summary>
    /// Loads the decrypted session from <c>ytmusic_session.dat</c>.
    /// If not present, transparently migrates any legacy plaintext <c>ytmusic_account_session.json</c>,
    /// writes the DPAPI-encrypted blob, and zero-overwrites + deletes the legacy file.
    /// </summary>
    public YouTubeAccountSessionData? Load()
    {
        if (File.Exists(_sessionFilePath))
        {
            byte[]? plainBytes = null;
            try
            {
                byte[] protectedBytes = File.ReadAllBytes(_sessionFilePath);
                if (protectedBytes.Length == 0)
                {
                    return null;
                }

                plainBytes = UnprotectBytes(protectedBytes);
                if (plainBytes is null || plainBytes.Length == 0)
                {
                    return null;
                }

                return ParseSessionJson(plainBytes);
            }
            catch
            {
                return null;
            }
            finally
            {
                if (plainBytes is not null)
                {
                    CryptographicOperations.ZeroMemory(plainBytes);
                }
            }
        }

        if (File.Exists(_legacyPlaintextSessionFilePath))
        {
            byte[]? legacyBytes = null;
            try
            {
                legacyBytes = File.ReadAllBytes(_legacyPlaintextSessionFilePath);
                var migrated = ParseSessionJson(legacyBytes);
                if (migrated is not null && !string.IsNullOrWhiteSpace(migrated.CookieHeader))
                {
                    Save(migrated);
                }

                SecureOverwriteAndDelete(_legacyPlaintextSessionFilePath);
                return migrated;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (legacyBytes is not null)
                {
                    CryptographicOperations.ZeroMemory(legacyBytes);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Encrypts <paramref name="session"/> with Windows DPAPI (current user scope + app entropy)
    /// and atomically writes it to <c>%LocalAppData%\XperienceMusicPlayer\Auth\ytmusic_session.dat</c>.
    /// </summary>
    public void Save(YouTubeAccountSessionData session)
    {
        ArgumentNullException.ThrowIfNull(session);

        Directory.CreateDirectory(_authDirectory);
        ApplyUserOnlyDirectoryAcl(_authDirectory);

        var payload = new Dictionary<string, string?>
        {
            ["CookieHeader"] = session.CookieHeader,
            ["AccountLabel"] = session.AccountLabel,
            ["VisitorData"] = session.VisitorData,
            ["DataSyncId"] = session.DataSyncId
        };

        byte[] plainBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        string tempFilePath = $"{_sessionFilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            byte[] protectedBytes = ProtectBytes(plainBytes);
            File.WriteAllBytes(tempFilePath, protectedBytes);
            File.Move(tempFilePath, _sessionFilePath, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBytes);
            try
            {
                if (File.Exists(tempFilePath))
                {
                    File.Delete(tempFilePath);
                }
            }
            catch
            {
                // Ignore temp cleanup failure
            }
        }
    }

    /// <summary>
    /// Overwrites with zeros and deletes both the DPAPI-encrypted session file and any legacy plaintext session file.
    /// </summary>
    public void Delete()
    {
        SecureOverwriteAndDelete(_sessionFilePath);
        SecureOverwriteAndDelete(_legacyPlaintextSessionFilePath);
    }

    /// <summary>
    /// Best-effort NTFS ACL hardening that restricts <paramref name="directoryPath"/> to the current Windows user
    /// and <c>SYSTEM</c> only, disabling inherited permissions.
    /// </summary>
    internal static void ApplyUserOnlyDirectoryAcl(string directoryPath)
    {
        try
        {
            if (!OperatingSystem.IsWindows() || !Directory.Exists(directoryPath))
            {
                return;
            }

            var currentUser = WindowsIdentity.GetCurrent().User;
            if (currentUser is null)
            {
                return;
            }

            var dirInfo = new DirectoryInfo(directoryPath);
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            const InheritanceFlags inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new FileSystemAccessRule(
                currentUser,
                FileSystemRights.FullControl,
                inheritance,
                PropagationFlags.None,
                AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl,
                inheritance,
                PropagationFlags.None,
                AccessControlType.Allow));

            dirInfo.SetAccessControl(security);
        }
        catch
        {
            // Never fail on non-NTFS or restricted filesystems
        }
    }

    private static YouTubeAccountSessionData? ParseSessionJson(byte[] utf8Json)
    {
        if (utf8Json.Length == 0)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(utf8Json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? savedCookies = root.TryGetProperty("CookieHeader", out var cProp) ? cProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(savedCookies))
        {
            return null;
        }

        string? savedLabel = root.TryGetProperty("AccountLabel", out var lProp) ? lProp.GetString() : null;
        string? savedVisitor = root.TryGetProperty("VisitorData", out var vProp) ? vProp.GetString() : null;
        string? savedDataSync = root.TryGetProperty("DataSyncId", out var dProp) ? dProp.GetString() : null;

        return new YouTubeAccountSessionData(savedCookies, savedLabel, savedVisitor, savedDataSync);
    }

    private static void SecureOverwriteAndDelete(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return;
        }

        try
        {
            long length = new FileInfo(filePath).Length;
            if (length > 0 && length <= 10 * 1024 * 1024)
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Write, FileShare.None);
                byte[] zeros = new byte[Math.Min(length, 4096)];
                long remaining = length;
                while (remaining > 0)
                {
                    int toWrite = (int)Math.Min(remaining, zeros.Length);
                    fs.Write(zeros, 0, toWrite);
                    remaining -= toWrite;
                }

                fs.Flush();
            }
        }
        catch
        {
            // Proceed to file deletion even if overwrite fails
        }

        try
        {
            File.Delete(filePath);
        }
        catch
        {
            // Ignore file delete errors
        }
    }

    private static byte[] ProtectBytes(byte[] plainBytes)
    {
        var inputHandle = GCHandle.Alloc(plainBytes, GCHandleType.Pinned);
        var entropyHandle = GCHandle.Alloc(AppEntropy, GCHandleType.Pinned);
        DATA_BLOB dataOut = default;

        try
        {
            var dataIn = new DATA_BLOB
            {
                cbData = plainBytes.Length,
                pbData = inputHandle.AddrOfPinnedObject()
            };

            var entropyBlob = new DATA_BLOB
            {
                cbData = AppEntropy.Length,
                pbData = entropyHandle.AddrOfPinnedObject()
            };

            if (!CryptProtectData(
                    ref dataIn,
                    szDataDescr: null,
                    ref entropyBlob,
                    pvReserved: IntPtr.Zero,
                    pPromptStruct: IntPtr.Zero,
                    dwFlags: CryptProtectUiForbidden,
                    out dataOut))
            {
                throw new CryptographicException(Marshal.GetLastWin32Error());
            }

            byte[] result = new byte[dataOut.cbData];
            Marshal.Copy(dataOut.pbData, result, 0, dataOut.cbData);
            return result;
        }
        finally
        {
            inputHandle.Free();
            entropyHandle.Free();
            if (dataOut.pbData != IntPtr.Zero)
            {
                LocalFree(dataOut.pbData);
            }
        }
    }

    private static byte[]? UnprotectBytes(byte[] protectedBytes)
    {
        var inputHandle = GCHandle.Alloc(protectedBytes, GCHandleType.Pinned);
        var entropyHandle = GCHandle.Alloc(AppEntropy, GCHandleType.Pinned);
        DATA_BLOB dataOut = default;

        try
        {
            var dataIn = new DATA_BLOB
            {
                cbData = protectedBytes.Length,
                pbData = inputHandle.AddrOfPinnedObject()
            };

            var entropyBlob = new DATA_BLOB
            {
                cbData = AppEntropy.Length,
                pbData = entropyHandle.AddrOfPinnedObject()
            };

            if (!CryptUnprotectData(
                    ref dataIn,
                    ppszDataDescr: IntPtr.Zero,
                    ref entropyBlob,
                    pvReserved: IntPtr.Zero,
                    pPromptStruct: IntPtr.Zero,
                    dwFlags: CryptProtectUiForbidden,
                    out dataOut))
            {
                return null;
            }

            byte[] result = new byte[dataOut.cbData];
            Marshal.Copy(dataOut.pbData, result, 0, dataOut.cbData);
            return result;
        }
        finally
        {
            inputHandle.Free();
            entropyHandle.Free();
            if (dataOut.pbData != IntPtr.Zero)
            {
                try
                {
                    if (dataOut.cbData > 0)
                    {
                        byte[] zeros = new byte[dataOut.cbData];
                        Marshal.Copy(zeros, 0, dataOut.pbData, dataOut.cbData);
                    }
                }
                catch
                {
                    // Ignore zeroing errors before free
                }

                LocalFree(dataOut.pbData);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn,
        string? szDataDescr,
        ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn,
        IntPtr ppszDataDescr,
        ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
