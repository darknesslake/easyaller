using System.Text.Json;

namespace Easyaller.App;

public sealed record OutlookArchiveHistoryEntry(
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    string ArchivePath,
    string Status,
    int InboxMoved,
    int SentMoved,
    IReadOnlyList<string> Errors);

public sealed class OutlookArchiveHistoryStore
{
    private const int MaximumEntries = 20;
    private readonly string _filePath;

    public OutlookArchiveHistoryStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
    }

    public IReadOnlyList<OutlookArchiveHistoryEntry> Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return [];
            }

            return JsonSerializer.Deserialize<List<OutlookArchiveHistoryEntry>>(File.ReadAllText(_filePath))
                ?.OrderByDescending(static entry => entry.StartedAt)
                .Take(MaximumEntries)
                .ToArray() ?? [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    public void Add(OutlookArchiveHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var entries = Load().Prepend(entry).Take(MaximumEntries).ToArray();
        var parent = Path.GetDirectoryName(_filePath)!;
        Directory.CreateDirectory(parent);
        var temporaryPath = _filePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, _filePath, overwrite: true);
    }

    public static OutlookArchiveHistoryStore CreateDefault()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Easyaller");
        return new OutlookArchiveHistoryStore(Path.Combine(root, "outlook-archive-history.json"));
    }
}
