using Easyaller.Core.Provisioning;

namespace Easyaller.Core.Tests;

public sealed class ProvisioningJournalTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "easyaller-journal-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Append_WritesOneLinePerEntry()
    {
        var journal = new ProvisioningJournal(Path.Combine(_directory, "journal.log"));

        journal.Append(CreateEntry("Применение профиля", "Completed"));
        journal.Append(CreateEntry("Проверка соответствия", "Соответствует"));

        var lines = File.ReadAllLines(journal.FilePath);
        Assert.Equal(2, lines.Length);
        Assert.Contains("Применение профиля", lines[0]);
        Assert.Contains("Проверка соответствия", lines[1]);
    }

    [Fact]
    public void Append_CreatesMissingDirectory()
    {
        var journal = new ProvisioningJournal(Path.Combine(_directory, "nested", "deeper", "journal.log"));

        journal.Append(CreateEntry("Применение профиля", "Completed"));

        Assert.True(File.Exists(journal.FilePath));
    }

    [Fact]
    public void Format_KeepsEntryOnASingleLine()
    {
        var line = ProvisioningJournal.Format(CreateEntry(
            "Применение профиля",
            "Completed",
            ["первая\nстрока", "вторая\tколонка"]));

        Assert.EndsWith("\n", line);
        Assert.Single(line.TrimEnd('\n').Split('\n'));
    }

    [Fact]
    public void Format_TruncatesOverlongDetail()
    {
        var line = ProvisioningJournal.Format(CreateEntry(
            "Применение профиля",
            "Completed",
            [new string('x', 900)]));

        Assert.Contains("…", line);
        Assert.True(line.Length < 900);
    }

    [Fact]
    public void Format_RecordsProfileIdentityAndRevision()
    {
        var profileId = Guid.NewGuid();
        var line = ProvisioningJournal.Format(new JournalEntry(
            DateTimeOffset.UnixEpoch,
            "PC01",
            profileId,
            7,
            "Test profile",
            "Применение профиля",
            "Completed",
            []));

        Assert.Contains(profileId.ToString(), line);
        Assert.Contains("rev.7", line);
        Assert.Contains("PC01", line);
    }

    [Fact]
    public void Format_EmptyValuesBecomePlaceholders()
    {
        var line = ProvisioningJournal.Format(new JournalEntry(
            DateTimeOffset.UnixEpoch,
            string.Empty,
            Guid.Empty,
            1,
            string.Empty,
            "Применение профиля",
            string.Empty,
            []));

        Assert.Contains("-", line);
    }

    private static JournalEntry CreateEntry(string action, string outcome, IReadOnlyList<string>? details = null) => new(
        DateTimeOffset.UnixEpoch,
        "PC01",
        Guid.NewGuid(),
        3,
        "Test profile",
        action,
        outcome,
        details ?? []);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
