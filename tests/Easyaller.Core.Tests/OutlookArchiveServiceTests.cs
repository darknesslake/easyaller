using Easyaller.App;

namespace Easyaller.Core.Tests;

public sealed class OutlookArchiveServiceTests
{
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
}
