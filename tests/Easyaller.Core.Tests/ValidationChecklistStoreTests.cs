using Easyaller.App;

namespace Easyaller.Core.Tests;

public sealed class ValidationChecklistStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "easyaller-validation-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void LoadOrCreate_ReturnsCompleteDefaultChecklist()
    {
        var state = new ValidationChecklistStore(Path.Combine(_root, "state.json")).LoadOrCreate();

        Assert.True(state.Entries.Count >= 12);
        Assert.All(state.Entries, entry => Assert.Equal(ValidationCheckStatus.Pending, entry.Status));
        Assert.Contains(state.Entries, entry => entry.Id == "outlook-archive");
    }

    [Fact]
    public void SaveAndLoad_RestoresProgressAndNotes()
    {
        var path = Path.Combine(_root, "state.json");
        var store = new ValidationChecklistStore(path);
        var state = store.LoadOrCreate();
        var entries = state.Entries.ToArray();
        entries[0] = entries[0] with { Status = ValidationCheckStatus.Failed, Notes = "Тестовая ошибка" };
        store.Save(new ValidationChecklistState(DateTimeOffset.Now, entries));

        var restored = new ValidationChecklistStore(path).LoadOrCreate();

        Assert.Equal(ValidationCheckStatus.Failed, restored.Entries[0].Status);
        Assert.Equal("Тестовая ошибка", restored.Entries[0].Notes);
    }

    [Fact]
    public void BuildReport_ContainsSummaryAndFailureNotes()
    {
        var entries = ValidationChecklistStore.CreateDefaultEntries().ToArray();
        entries[0] = entries[0] with { Status = ValidationCheckStatus.Failed, Notes = "Код 5" };

        var report = ValidationChecklistStore.BuildReport(
            new ValidationChecklistState(DateTimeOffset.Now, entries),
            "TEST-PC");

        Assert.Contains("Компьютер: TEST-PC", report);
        Assert.Contains("Ошибок: 1", report);
        Assert.Contains("Заметки: Код 5", report);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
