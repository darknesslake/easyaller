using System.Text;

namespace Easyaller.Core.Provisioning;

public sealed record JournalEntry(
    DateTimeOffset TimestampUtc,
    string MachineName,
    Guid ProfileId,
    int ProfileRevision,
    string ProfileName,
    string Action,
    string Outcome,
    IReadOnlyList<string> Details);

/// <summary>
/// Append-only local record of what Easyaller did on this machine.
/// It intentionally stores no password, proxy address, or domain credential, matching the
/// same boundary the resume state uses.
/// </summary>
public sealed class ProvisioningJournal
{
    private const int MaxDetailLength = 400;
    private readonly object _writeLock = new();

    public ProvisioningJournal(string? journalFilePath = null)
    {
        FilePath = journalFilePath ?? GetDefaultPath();
    }

    public string FilePath { get; }

    public void Append(JournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var line = Format(entry);
        lock (_writeLock)
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.AppendAllText(FilePath, line, Encoding.UTF8);
        }
    }

    public static string Format(JournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var text = new StringBuilder();
        text.Append(entry.TimestampUtc.ToString("yyyy-MM-dd HH:mm:ss zzz"));
        text.Append('\t').Append(Sanitize(entry.MachineName));
        text.Append('\t').Append(entry.ProfileId);
        text.Append("\trev.").Append(entry.ProfileRevision);
        text.Append('\t').Append(Sanitize(entry.ProfileName));
        text.Append('\t').Append(Sanitize(entry.Action));
        text.Append('\t').Append(Sanitize(entry.Outcome));
        if (entry.Details.Count > 0)
        {
            text.Append('\t').Append(string.Join("; ", entry.Details.Select(Sanitize)));
        }

        text.Append('\n');
        return text.ToString();
    }

    /// <summary>Keeps one entry on one line and bounds its length, so the journal stays readable.</summary>
    private static string Sanitize(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "-";
        }

        var collapsed = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            collapsed.Append(character is '\r' or '\n' or '\t' ? ' ' : character);
        }

        var text = collapsed.ToString().Trim();
        return text.Length <= MaxDetailLength ? text : text[..MaxDetailLength] + "…";
    }

    private static string GetDefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Easyaller",
        "provisioning-journal.log");
}
