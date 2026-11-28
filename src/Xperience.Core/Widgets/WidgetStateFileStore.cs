using System.Text.Json;

namespace Xperience.Core.Widgets;

/// <summary>
/// Process-safe, atomic JSON persistence for <see cref="WidgetStateSnapshot"/> at
/// <c>%LOCALAPPDATA%\XperienceMusicPlayer\widget_state.json</c>.
/// </summary>
public sealed class WidgetStateFileStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly object _ioLock = new();

    public string FilePath { get; }

    public WidgetStateFileStore(string? filePath = null)
    {
        FilePath = string.IsNullOrWhiteSpace(filePath)
            ? GetDefaultStateFilePath()
            : filePath;
    }

    public static string GetDefaultStateFilePath()
    {
        string baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XperienceMusicPlayer");
        return Path.Combine(baseDir, "widget_state.json");
    }

    public WidgetStateSnapshot Load()
    {
        lock (_ioLock)
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    return new WidgetStateSnapshot();
                }

                string json = File.ReadAllText(FilePath);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return new WidgetStateSnapshot();
                }

                return JsonSerializer.Deserialize<WidgetStateSnapshot>(json, SerializerOptions)
                       ?? new WidgetStateSnapshot();
            }
            catch
            {
                return new WidgetStateSnapshot();
            }
        }
    }

    public void Save(WidgetStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (_ioLock)
        {
            string? dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = JsonSerializer.Serialize(snapshot, SerializerOptions);
            string tempPath = $"{FilePath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";

            try
            {
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, FilePath, overwrite: true);
            }
            catch
            {
                try
                {
                    File.WriteAllText(FilePath, json);
                }
                catch
                {
                    // Best-effort persistence: never crash host process on transient file lock
                }
                finally
                {
                    try
                    {
                        if (File.Exists(tempPath))
                        {
                            File.Delete(tempPath);
                        }
                    }
                    catch
                    {
                        // Ignore cleanup failure
                    }
                }
            }
        }
    }
}
