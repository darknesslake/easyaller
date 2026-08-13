using System.Text.Json;

namespace Easyaller.App;

public sealed record MaintenanceSettings(string? ShortcutSourceDirectory);

/// <summary>Stores non-profile UI choices for maintenance operations on this Windows account.</summary>
public sealed class MaintenanceSettingsStore
{
    private readonly string _filePath;

    public MaintenanceSettingsStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
    }

    public string? LoadShortcutSource()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return null;
            }

            var settings = JsonSerializer.Deserialize<MaintenanceSettings>(File.ReadAllText(_filePath));
            var path = settings?.ShortcutSourceDirectory?.Trim();
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
            {
                return path;
            }

            Clear();
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Clear();
            return null;
        }
    }

    public void SaveShortcutSource(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Directory.Exists(directory))
        {
            Clear();
            return;
        }

        var parent = Path.GetDirectoryName(_filePath)!;
        Directory.CreateDirectory(parent);
        var temporaryPath = _filePath + ".tmp";
        var json = JsonSerializer.Serialize(
            new MaintenanceSettings(Path.GetFullPath(directory)),
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, _filePath, overwrite: true);
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                File.Delete(_filePath);
            }

            var temporaryPath = _filePath + ".tmp";
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A stale setting is harmless. The UI still resets because Load returns null.
        }
    }

    public static MaintenanceSettingsStore CreateDefault()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Easyaller");
        return new MaintenanceSettingsStore(Path.Combine(root, "maintenance-settings.json"));
    }
}
