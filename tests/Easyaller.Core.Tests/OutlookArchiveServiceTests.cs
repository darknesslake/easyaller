using Easyaller.App;

namespace Easyaller.Core.Tests;

public sealed class OutlookArchiveServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "easyaller-outlook-tests-" + Guid.NewGuid().ToString("N"));
    [Fact]
    public void CalculateCutoff_SubtractsSelectedMonths()
    {
        var now = new DateTime(2026, 8, 13, 10, 30, 0);

        var cutoff = OutlookArchiveService.CalculateCutoff(now, 12);

        Assert.Equal(new DateTime(2025, 8, 13, 10, 30, 0), cutoff);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(121)]
    public void CalculateCutoff_RejectsUnsafeRange(int months)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            OutlookArchiveService.CalculateCutoff(DateTime.Now, months));
    }

    [Fact]
    public void GetDefaultArchivePath_UsesTodaysDateAndOutlookFolder()
    {
        var documents = Path.Combine(Path.GetTempPath(), "documents-" + Guid.NewGuid().ToString("N"));

        var result = OutlookArchiveService.GetDefaultArchivePath(new DateTime(2026, 8, 13), documents);

        Assert.Equal(Path.Combine(documents, "Файлы Outlook", "13.08.2026.pst"), result);
    }

    [Theory]
    [InlineData(OutlookArchiveAge.AllTime, "2026-08-13")]
    [InlineData(OutlookArchiveAge.TwoWeeks, "2026-07-30")]
    [InlineData(OutlookArchiveAge.OneMonth, "2026-07-13")]
    [InlineData(OutlookArchiveAge.ThreeMonths, "2026-05-13")]
    public void CalculateCutoff_UsesOnlySupportedArchivePeriods(OutlookArchiveAge age, string expectedDate)
    {
        var now = new DateTime(2026, 8, 13, 10, 0, 0);

        var result = OutlookArchiveService.CalculateCutoff(now, age);

        Assert.Equal(expectedDate, result.ToString("yyyy-MM-dd"));
    }

    [Fact]
    public void ArchiveProgress_ReportsFolderAndCounters()
    {
        var progress = new OutlookArchiveProgress("Входящие", 7, 20, 6, 1);

        Assert.Equal("Входящие", progress.FolderName);
        Assert.Equal(7, progress.ProcessedMessages);
        Assert.Equal(20, progress.TotalMessages);
        Assert.Equal(6, progress.MovedMessages);
        Assert.Equal(1, progress.FailedMessages);
    }

    [Fact]
    public void ArchiveResult_ExposesFolderSpecificFailures()
    {
        var result = new OutlookArchiveResult(
            "Входящие",
            3,
            2,
            @"C:\Archive\13.08.2026.pst",
            [new OutlookArchiveFailure("«Тема письма»", "Нет доступа")],
            WasCancelled: false);

        Assert.Equal("Входящие", result.FolderName);
        Assert.Equal(2, result.MovedMessages);
        Assert.Equal("«Тема письма»: Нет доступа", Assert.Single(result.Errors));
    }

    [Fact]
    public void HistoryStore_PersistsNewestEntriesAcrossRestart()
    {
        var path = Path.Combine(_root, "history.json");
        var store = new OutlookArchiveHistoryStore(path);
        var first = new OutlookArchiveHistoryEntry(
            new DateTimeOffset(2026, 8, 13, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 13, 9, 1, 0, TimeSpan.Zero),
            @"C:\Archive\13.08.2026.pst",
            "завершена",
            5,
            4,
            []);
        var second = first with
        {
            StartedAt = first.StartedAt.AddHours(1),
            FinishedAt = first.FinishedAt.AddHours(1),
            Status = "остановлена пользователем",
        };

        store.Add(first);
        store.Add(second);

        var restored = new OutlookArchiveHistoryStore(path).Load();
        Assert.Equal(2, restored.Count);
        Assert.Equal("остановлена пользователем", restored[0].Status);
        Assert.Equal("завершена", restored[1].Status);
    }

    [Fact]
    public void HistoryStore_UsesAtMostTwentyEntries()
    {
        var path = Path.Combine(_root, "history.json");
        var store = new OutlookArchiveHistoryStore(path);
        for (var index = 0; index < 25; index++)
        {
            store.Add(new OutlookArchiveHistoryEntry(
                DateTimeOffset.UtcNow.AddMinutes(index),
                DateTimeOffset.UtcNow.AddMinutes(index + 1),
                $@"C:\Archive\{index}.pst",
                "завершена",
                index,
                0,
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
