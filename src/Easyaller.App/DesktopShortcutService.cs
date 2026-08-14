namespace Easyaller.App;

public enum ShortcutConflictBehavior
{
    Skip,
    Replace,
}

public sealed record LocalWindowsUser(string Name, string ProfileDirectory)
{
    public string DesktopDirectory => DesktopShortcutService.ResolveDesktopDirectory(ProfileDirectory);
}

public enum DesktopShortcutAction
{
    Copy,
    Replace,
    Skip,
    Failed,
}

public sealed record DesktopShortcutPlanItem(string FileName, string DestinationPath, DesktopShortcutAction Action);

public sealed record DesktopShortcutAccessCheck(bool CanWrite, string Message);

public sealed record DesktopShortcutCopyItem(string FileName, DesktopShortcutAction Action, string? Error = null);

public sealed record DesktopShortcutCopyResult(
    int Copied,
    int Replaced,
    int Skipped,
    IReadOnlyList<string> Errors,
    IReadOnlyList<DesktopShortcutCopyItem> Items);

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

    public IReadOnlyList<DesktopShortcutPlanItem> BuildPlan(
        string sourceDirectory,
        string targetDesktopDirectory,
        ShortcutConflictBehavior conflictBehavior) =>
        Discover(sourceDirectory)
            .Select(source =>
            {
                var fileName = Path.GetFileName(source);
                var destination = Path.Combine(targetDesktopDirectory, fileName);
                var action = !File.Exists(destination)
                    ? DesktopShortcutAction.Copy
                    : conflictBehavior == ShortcutConflictBehavior.Replace
                        ? DesktopShortcutAction.Replace
                        : DesktopShortcutAction.Skip;
                return new DesktopShortcutPlanItem(fileName, destination, action);
            })
            .ToArray();

    public DesktopShortcutAccessCheck CheckTargetAccess(string targetDesktopDirectory)
    {
        if (string.IsNullOrWhiteSpace(targetDesktopDirectory) || !Directory.Exists(targetDesktopDirectory))
        {
            return new DesktopShortcutAccessCheck(false, "Рабочий стол пользователя не найден.");
        }

        var probePath = Path.Combine(targetDesktopDirectory, $".easyaller-access-{Guid.NewGuid():N}.tmp");
        try
        {
            using (File.Create(probePath, 1, FileOptions.DeleteOnClose))
            {
            }

            return new DesktopShortcutAccessCheck(true, "Доступ на запись подтверждён.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new DesktopShortcutAccessCheck(
                false,
                "Нет доступа на запись. Перезапустите Easyaller с правами администратора. " + exception.Message);
        }
        finally
        {
            try
            {
                File.Delete(probePath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // FileOptions.DeleteOnClose normally removes the probe. A failed cleanup is non-fatal.
            }
        }
    }

    public DesktopShortcutCopyResult Copy(
        string sourceDirectory,
        string targetDesktopDirectory,
        ShortcutConflictBehavior conflictBehavior)
    {
        var shortcuts = Discover(sourceDirectory);
        if (shortcuts.Count == 0)
        {
            return new DesktopShortcutCopyResult(0, 0, 0, ["В папке-источнике нет ярлыков .lnk, .url или .website."], []);
        }

        var copied = 0;
        var replaced = 0;
        var skipped = 0;
        var errors = new List<string>();
        var items = new List<DesktopShortcutCopyItem>();

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
                [$"Не удалось открыть рабочий стол выбранного пользователя. Перезапустите Easyaller с правами администратора. {exception.Message}"],
                []);
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
                        items.Add(new DesktopShortcutCopyItem(Path.GetFileName(source), DesktopShortcutAction.Skip));
                        continue;
                    }

                    File.Copy(source, destination, overwrite: true);
                    replaced++;
                    items.Add(new DesktopShortcutCopyItem(Path.GetFileName(source), DesktopShortcutAction.Replace));
                    continue;
                }

                File.Copy(source, destination, overwrite: false);
                copied++;
                items.Add(new DesktopShortcutCopyItem(Path.GetFileName(source), DesktopShortcutAction.Copy));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{Path.GetFileName(source)}: {exception.Message}");
                items.Add(new DesktopShortcutCopyItem(Path.GetFileName(source), DesktopShortcutAction.Failed, exception.Message));
            }
        }

        return new DesktopShortcutCopyResult(copied, replaced, skipped, errors, items);
    }

    public static string ResolveDesktopDirectory(string profileDirectory)
    {
        var candidates = new[]
        {
            Path.Combine(profileDirectory, "Desktop"),
            Path.Combine(profileDirectory, "Рабочий стол"),
            Path.Combine(profileDirectory, "OneDrive", "Desktop"),
            Path.Combine(profileDirectory, "OneDrive", "Рабочий стол"),
        };
        return candidates.FirstOrDefault(Directory.Exists) ?? candidates[0];
    }

    private static bool IsSystemProfile(string name) =>
        name.Equals("All Users", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Default", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Default User", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Public", StringComparison.OrdinalIgnoreCase)
        || name.Equals("defaultuser0", StringComparison.OrdinalIgnoreCase);
}
