namespace Easyaller.App;

public enum ShortcutConflictBehavior
{
    Skip,
    Replace,
}

public sealed record LocalWindowsUser(string Name, string ProfileDirectory)
{
    public string DesktopDirectory => Path.Combine(ProfileDirectory, "Desktop");
}

public sealed record DesktopShortcutCopyResult(int Copied, int Replaced, int Skipped, IReadOnlyList<string> Errors);

/// <summary>
/// A standalone maintenance operation. It deliberately accepts only Windows shortcut formats
/// and never executes their targets or changes a provisioning profile.
/// </summary>
public sealed class DesktopShortcutService
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".lnk", ".url", ".website",
    };

    public IReadOnlyList<LocalWindowsUser> GetUsers(string usersRoot)
    {
        if (!Directory.Exists(usersRoot))
        {
            return [];
        }

        return Directory.EnumerateDirectories(usersRoot)
            .Select(path => new LocalWindowsUser(Path.GetFileName(path), path))
            .Where(static user => !IsSystemProfile(user.Name))
            .Where(static user => Directory.Exists(user.ProfileDirectory))
            .OrderBy(static user => user.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<string> Discover(string sourceDirectory)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            return [];
        }

        return Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => SupportedExtensions.Contains(Path.GetExtension(path)))
            .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public DesktopShortcutCopyResult Copy(
        string sourceDirectory,
        string targetDesktopDirectory,
        ShortcutConflictBehavior conflictBehavior)
    {
        var shortcuts = Discover(sourceDirectory);
        if (shortcuts.Count == 0)
        {
            return new DesktopShortcutCopyResult(0, 0, 0, ["В папке-источнике нет ярлыков .lnk, .url или .website."]);
        }

        var copied = 0;
        var replaced = 0;
        var skipped = 0;
        var errors = new List<string>();

        try
        {
            Directory.CreateDirectory(targetDesktopDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new DesktopShortcutCopyResult(
                0,
                0,
                0,
                [$"Не удалось открыть рабочий стол выбранного пользователя. Перезапустите Easyaller с правами администратора. {exception.Message}"]);
        }

        foreach (var source in shortcuts)
        {
            var destination = Path.Combine(targetDesktopDirectory, Path.GetFileName(source));
            try
            {
                if (File.Exists(destination))
                {
                    if (conflictBehavior == ShortcutConflictBehavior.Skip)
                    {
                        skipped++;
                        continue;
                    }

                    File.Copy(source, destination, overwrite: true);
                    replaced++;
                    continue;
                }

                File.Copy(source, destination, overwrite: false);
                copied++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{Path.GetFileName(source)}: {exception.Message}");
            }
        }

        return new DesktopShortcutCopyResult(copied, replaced, skipped, errors);
    }

    private static bool IsSystemProfile(string name) =>
        name.Equals("All Users", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Default", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Default User", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Public", StringComparison.OrdinalIgnoreCase)
        || name.Equals("defaultuser0", StringComparison.OrdinalIgnoreCase);
}
