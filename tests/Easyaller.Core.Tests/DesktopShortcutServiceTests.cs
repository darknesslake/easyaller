using Easyaller.App;

namespace Easyaller.Core.Tests;

public sealed class DesktopShortcutServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "easyaller-shortcuts-" + Guid.NewGuid().ToString("N"));
    private readonly DesktopShortcutService _service = new();

    [Fact]
    public void Discover_ReturnsOnlySupportedShortcutFiles()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "Ярлыки")).FullName;
        File.WriteAllText(Path.Combine(source, "Browser.lnk"), "shortcut");
        File.WriteAllText(Path.Combine(source, "Portal.url"), "url");
        File.WriteAllText(Path.Combine(source, "readme.txt"), "ignored");

        var result = _service.Discover(source);

        Assert.Equal(2, result.Count);
        Assert.DoesNotContain(result, path => path.EndsWith("readme.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void Copy_SkipsExistingShortcut_WhenRequested()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        var desktop = Directory.CreateDirectory(Path.Combine(_root, "user", "Desktop")).FullName;
        File.WriteAllText(Path.Combine(source, "App.lnk"), "new");
        File.WriteAllText(Path.Combine(desktop, "App.lnk"), "old");

        var result = _service.Copy(source, desktop, ShortcutConflictBehavior.Skip);

        Assert.Equal(1, result.Skipped);
        Assert.Equal("old", File.ReadAllText(Path.Combine(desktop, "App.lnk")));
    }

    [Fact]
    public void Copy_ReplacesExistingShortcut_WhenRequested()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        var desktop = Directory.CreateDirectory(Path.Combine(_root, "user", "Desktop")).FullName;
        File.WriteAllText(Path.Combine(source, "App.lnk"), "new");
        File.WriteAllText(Path.Combine(desktop, "App.lnk"), "old");

        var result = _service.Copy(source, desktop, ShortcutConflictBehavior.Replace);

        Assert.Equal(1, result.Replaced);
        Assert.Equal("new", File.ReadAllText(Path.Combine(desktop, "App.lnk")));
    }

    [Fact]
    public void BuildPlan_SeparatesNewReplacedAndSkippedFiles()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "source-plan")).FullName;
        var desktop = Directory.CreateDirectory(Path.Combine(_root, "user-plan", "Desktop")).FullName;
        File.WriteAllText(Path.Combine(source, "New.lnk"), "new");
        File.WriteAllText(Path.Combine(source, "Existing.lnk"), "replacement");
        File.WriteAllText(Path.Combine(desktop, "Existing.lnk"), "current");

        var skipPlan = _service.BuildPlan(source, desktop, ShortcutConflictBehavior.Skip);
        var replacePlan = _service.BuildPlan(source, desktop, ShortcutConflictBehavior.Replace);

        Assert.Contains(skipPlan, item => item.FileName == "New.lnk" && item.Action == DesktopShortcutAction.Copy);
        Assert.Contains(skipPlan, item => item.FileName == "Existing.lnk" && item.Action == DesktopShortcutAction.Skip);
        Assert.Contains(replacePlan, item => item.FileName == "Existing.lnk" && item.Action == DesktopShortcutAction.Replace);
    }

    [Fact]
    public void CheckTargetAccess_ConfirmsWritableDesktop()
    {
        var desktop = Directory.CreateDirectory(Path.Combine(_root, "access-user", "Desktop")).FullName;

        var result = _service.CheckTargetAccess(desktop);

        Assert.True(result.CanWrite);
        Assert.Empty(Directory.EnumerateFiles(desktop));
    }

    [Fact]
    public void CheckTargetAccess_RejectsMissingDesktop()
    {
        var result = _service.CheckTargetAccess(Path.Combine(_root, "missing", "Desktop"));

        Assert.False(result.CanWrite);
        Assert.Contains("не найден", result.Message);
    }

    [Fact]
    public void Copy_ReturnsPerFileResultDetails()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "source-details")).FullName;
        var desktop = Directory.CreateDirectory(Path.Combine(_root, "user-details", "Desktop")).FullName;
        File.WriteAllText(Path.Combine(source, "New.lnk"), "new");
        File.WriteAllText(Path.Combine(source, "Existing.url"), "replacement");
        File.WriteAllText(Path.Combine(desktop, "Existing.url"), "current");

        var result = _service.Copy(source, desktop, ShortcutConflictBehavior.Skip);

        Assert.Contains(result.Items, item => item.FileName == "New.lnk" && item.Action == DesktopShortcutAction.Copy);
        Assert.Contains(result.Items, item => item.FileName == "Existing.url" && item.Action == DesktopShortcutAction.Skip);
    }

    [Fact]
    public void ResolveDesktopDirectory_UsesExistingLocalizedDesktop()
    {
        var profile = Directory.CreateDirectory(Path.Combine(_root, "localized-user")).FullName;
        var localizedDesktop = Directory.CreateDirectory(Path.Combine(profile, "Рабочий стол")).FullName;

        Assert.Equal(localizedDesktop, DesktopShortcutService.ResolveDesktopDirectory(profile));
    }

    [Fact]
    public void GetUsers_ExcludesSharedAndSystemProfiles()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Public"));
        Directory.CreateDirectory(Path.Combine(_root, "Default"));
        Directory.CreateDirectory(Path.Combine(_root, "operator"));

        var users = _service.GetUsers(_root);

        Assert.Collection(users, user => Assert.Equal("operator", user.Name));
    }

    [Fact]
    public void Copy_ReturnsReadableError_WhenDesktopCannotBeCreated()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        File.WriteAllText(Path.Combine(source, "App.lnk"), "shortcut");
        var fileInsteadOfDirectory = Path.Combine(_root, "not-a-directory");
        File.WriteAllText(fileInsteadOfDirectory, "occupied");

        var result = _service.Copy(source, fileInsteadOfDirectory, ShortcutConflictBehavior.Skip);

        Assert.DoesNotContain(result.Errors, string.IsNullOrWhiteSpace);
        Assert.Single(result.Errors);
        Assert.Contains("правами администратора", result.Errors[0]);
        Assert.Equal(0, result.Copied);
    }

    [Fact]
    public void SettingsStore_RestoresExistingShortcutDirectory()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "Ярлыки")).FullName;
        var settingsPath = Path.Combine(_root, "settings", "maintenance.json");
        var store = new MaintenanceSettingsStore(settingsPath);

        store.SaveShortcutSource(source);

        Assert.Equal(Path.GetFullPath(source), new MaintenanceSettingsStore(settingsPath).LoadShortcutSource());
    }

    [Fact]
    public void SettingsStore_ClearsSavedDirectory_WhenItNoLongerExists()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "Ярлыки")).FullName;
        var settingsPath = Path.Combine(_root, "settings", "maintenance.json");
        var store = new MaintenanceSettingsStore(settingsPath);
        store.SaveShortcutSource(source);
        Directory.Delete(source);

        Assert.Null(store.LoadShortcutSource());
        Assert.False(File.Exists(settingsPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
