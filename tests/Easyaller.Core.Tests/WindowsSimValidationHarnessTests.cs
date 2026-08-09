namespace Easyaller.Core.Tests;

public sealed class WindowsSimValidationHarnessTests
{
    [Fact]
    public void ValidationHarness_RecordsEvidenceWithoutDestructiveCommands()
    {
        var scriptPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Validate-AnswerFile.ps1");
        var script = File.ReadAllText(scriptPath);

        Assert.Contains("InstallationMedia", script, StringComparison.Ordinal);
        Assert.Contains("WindowsImage", script, StringComparison.Ordinal);
        Assert.Contains("ImageIndex", script, StringComparison.Ordinal);
        Assert.Contains("WindowsSimResult", script, StringComparison.Ordinal);
        Assert.Contains("Get-FileHash", script, StringComparison.Ordinal);
        Assert.Contains("dism.exe", script, StringComparison.Ordinal);
        Assert.Contains("RequiresManualConfirmation", script, StringComparison.Ordinal);
        Assert.DoesNotContain("/Mount-Image", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Format-Volume", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("diskpart", script, StringComparison.OrdinalIgnoreCase);
    }
}
