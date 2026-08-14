using System.Text.Json;
using Easyaller.Deployment;

namespace Easyaller.App;

public sealed record ApplicationInstallQueueState(
    DateTimeOffset UpdatedAt,
    string PackageRootDirectory,
    string DestinationDirectory,
    IReadOnlyList<ApplicationInstallStep> RemainingSteps,
    string FailedApplicationName,
    IReadOnlyList<string> LogEntries);

public sealed class ApplicationInstallQueueStore
{
    private readonly string _filePath;

    public ApplicationInstallQueueStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
    }

    public ApplicationInstallQueueState? Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return null;
            }

            var state = JsonSerializer.Deserialize<ApplicationInstallQueueState>(File.ReadAllText(_filePath));
            return state is { RemainingSteps.Count: > 0 } ? state : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Save(ApplicationInstallQueueState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.RemainingSteps.Count == 0)
        {
            Clear();
            return;
        }

        var parent = Path.GetDirectoryName(_filePath)!;
        Directory.CreateDirectory(parent);
        var temporaryPath = _filePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, _filePath, overwrite: true);
    }

    public void Clear()
    {
        try
        {
            File.Delete(_filePath);
            File.Delete(_filePath + ".tmp");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A stale resume file is surfaced again on the next launch instead of risking data loss.
        }
    }

    public static ApplicationInstallQueueStore CreateDefault()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Easyaller");
        return new ApplicationInstallQueueStore(Path.Combine(root, "application-install-queue.json"));
    }
}
