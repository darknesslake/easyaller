using Easyaller.App;
using Easyaller.Deployment;

namespace Easyaller.Core.Tests;

public sealed class ApplicationInstallQueueStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "easyaller-queue-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void SaveAndLoad_RestoresRemainingStepsAndLog()
    {
        var path = Path.Combine(_root, "queue.json");
        var store = new ApplicationInstallQueueStore(path);
        var step = new ApplicationInstallStep("browser", "Browser", @"C:\Installers\browser.exe", ["/S"]);
        store.Save(new ApplicationInstallQueueState(
            DateTimeOffset.UtcNow,
            @"C:\Installers",
            @"C:\Desktop\Easyaller-установщики",
            [step],
            "Browser",
            ["Browser | ошибка | код 1603"]));

        var restored = new ApplicationInstallQueueStore(path).Load();

        Assert.NotNull(restored);
        Assert.Equal("Browser", Assert.Single(restored.RemainingSteps).DisplayName);
        Assert.Single(restored.LogEntries);
    }

    [Fact]
    public void Save_EmptyQueueClearsState()
    {
        var path = Path.Combine(_root, "queue.json");
        var store = new ApplicationInstallQueueStore(path);
        var initial = new ApplicationInstallQueueState(
            DateTimeOffset.UtcNow,
            @"C:\Installers",
            @"C:\Destination",
            [new ApplicationInstallStep("app", "App", @"C:\Installers\app.exe", [])],
            "App",
            []);
        store.Save(initial);

        store.Save(initial with { RemainingSteps = [] });

        Assert.Null(store.Load());
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Load_InvalidJsonDoesNotCrashApplication()
    {
        var path = Path.Combine(_root, "queue.json");
        Directory.CreateDirectory(_root);
        File.WriteAllText(path, "not-json");

        Assert.Null(new ApplicationInstallQueueStore(path).Load());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
