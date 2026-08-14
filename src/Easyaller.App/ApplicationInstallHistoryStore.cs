using System.Text.Json;

namespace Easyaller.App;

public sealed record ApplicationInstallHistoryEntry(
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    string Status,
    int InstalledCount,
    string? ProblemApplication,
    IReadOnlyList<string> LogEntries);

public sealed class ApplicationInstallHistoryStore
{
    private const int MaximumEntries = 20;
    private readonly string _filePath;

    public ApplicationInstallHistoryStore(string filePath) => _filePath = filePath;

    public IReadOnlyList<ApplicationInstallHistoryEntry> Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return [];
            }

            return JsonSerializer.Deserialize<List<ApplicationInstallHistoryEntry>>(File.ReadAllText(_filePath))
                ?.OrderByDescending(static entry => entry.StartedAt)
                .Take(MaximumEntries)
                .ToArray() ?? [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    public void Add(ApplicationInstallHistoryEntry entry)
    {
        var entries = Load().Prepend(entry).Take(MaximumEntries).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var temporaryPath = _filePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, _filePath, overwrite: true);
    }

    public static ApplicationInstallHistoryStore CreateDefault()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Easyaller");
        return new ApplicationInstallHistoryStore(Path.Combine(root, "application-install-history.json"));
    }
}
