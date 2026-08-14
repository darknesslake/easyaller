using Easyaller.App;

namespace Easyaller.Core.Tests;

public sealed class ApplicationInstallHistoryStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "easyaller-install-history-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Add_PersistsNewestEntriesAcrossRestart()
    {
        var path = Path.Combine(_root, "history.json");
        var store = new ApplicationInstallHistoryStore(path);
        var first = new ApplicationInstallHistoryEntry(
            DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow.AddMinutes(-50),
            "завершена",
            3,
            null,
            ["App | установлено"]);
        var second = first with
        {
            StartedAt = DateTimeOffset.UtcNow,
            FinishedAt = DateTimeOffset.UtcNow,
            Status = "остановлена пользователем",
            ProblemApplication = "Browser",
        };

        store.Add(first);
        store.Add(second);

        var result = new ApplicationInstallHistoryStore(path).Load();
        Assert.Equal(2, result.Count);
        Assert.Equal("остановлена пользователем", result[0].Status);
        Assert.Equal("завершена", result[1].Status);
    }

    [Fact]
    public void Add_KeepsOnlyTwentyEntries()
    {
        var path = Path.Combine(_root, "history.json");
        var store = new ApplicationInstallHistoryStore(path);
        for (var index = 0; index < 25; index++)
        {
            store.Add(new ApplicationInstallHistoryEntry(
                DateTimeOffset.UtcNow.AddMinutes(index),
                DateTimeOffset.UtcNow.AddMinutes(index + 1),
                "завершена",
                index,
                null,
                []));
        }

        Assert.Equal(20, store.Load().Count);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
