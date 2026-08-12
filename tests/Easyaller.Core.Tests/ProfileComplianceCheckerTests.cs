using Easyaller.Core.Profiles;
using Easyaller.Core.Provisioning;

namespace Easyaller.Core.Tests;

public sealed class ProfileComplianceCheckerTests
{
    private readonly ProfileComplianceChecker _checker = new();

    [Fact]
    public void Check_MatchingMachine_ReportsCompliant()
    {
        var profile = CreateProfile();
        var machine = CreateMachine();

        var report = _checker.Check(profile, machine, DateTimeOffset.UnixEpoch);

        Assert.True(report.IsCompliant);
        Assert.Equal(0, report.MismatchCount);
        Assert.All(
            report.Checks.Where(static check => check.Status != ComplianceStatus.NotConfigured),
            static check => Assert.Equal(ComplianceStatus.Match, check.Status));
    }

    [Fact]
    public void Check_InstalledApplicationWithShortcut_ReportsMatch()
    {
        var profile = CreateProfile() with
        {
            Applications = [new ApplicationProfile("seven-zip", "7-Zip", ApplicationSourceKind.PackageRelative, "7z.msi", [])],
        };
        // Installers register a decorated name, so matching must tolerate the version suffix.
        var installed = new InstalledSoftwareSnapshot(
            [new InstalledSoftwareEntry("7-Zip 24.09 (x64)", "24.09")],
            [new InstalledShortcutEntry("7-Zip File Manager", @"C:\Program Files\7-Zip\7zFM.exe")]);

        var report = _checker.Check(profile, CreateMachine(), DateTimeOffset.UnixEpoch, installed);

        var check = Assert.Single(report.Checks, entry => entry.Title == "Приложение: 7-Zip");
        Assert.Equal(ComplianceStatus.Match, check.Status);
        Assert.Contains("24.09", check.Actual, StringComparison.Ordinal);
        Assert.Contains("ярлык есть", check.Actual, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_InstalledApplicationWithoutShortcut_SaysShortcutMissing()
    {
        var profile = CreateProfile() with
        {
            Applications = [new ApplicationProfile("seven-zip", "7-Zip", ApplicationSourceKind.PackageRelative, "7z.msi", [])],
        };
        var installed = new InstalledSoftwareSnapshot([new InstalledSoftwareEntry("7-Zip 24.09 (x64)", "24.09")], []);

        var report = _checker.Check(profile, CreateMachine(), DateTimeOffset.UnixEpoch, installed);

        var check = Assert.Single(report.Checks, entry => entry.Title == "Приложение: 7-Zip");
        Assert.Equal(ComplianceStatus.Match, check.Status);
        Assert.Contains("ярлыка нет", check.Actual, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_FootprintGrewSinceReference_StaysMatch()
    {
        // Caches, logs and updates make a folder grow; that must not read as corruption.
        var report = CheckFootprint(
            reference: new ApplicationFootprint(@"C:\Program Files\7-Zip", 5_000_000, 40),
            actualSize: 6_500_000,
            actualFileCount: 44);

        Assert.Equal(ComplianceStatus.Match, report.Status);
    }

    [Fact]
    public void Check_FootprintLostFiles_ReportsMismatch()
    {
        var report = CheckFootprint(
            reference: new ApplicationFootprint(@"C:\Program Files\7-Zip", 5_000_000, 40),
            actualSize: 4_900_000,
            actualFileCount: 31);

        Assert.Equal(ComplianceStatus.Mismatch, report.Status);
        Assert.Contains("меньше эталона", report.Actual, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_FootprintShrankNoticeably_ReportsMismatch()
    {
        var report = CheckFootprint(
            reference: new ApplicationFootprint(@"C:\Program Files\7-Zip", 5_000_000, 40),
            actualSize: 2_000_000,
            actualFileCount: 40);

        Assert.Equal(ComplianceStatus.Mismatch, report.Status);
    }

    [Fact]
    public void Check_FootprintFolderUnreadable_ReportsUnknown()
    {
        var report = CheckFootprint(
            reference: new ApplicationFootprint(@"C:\Program Files\7-Zip", 5_000_000, 40),
            actualSize: 0,
            actualFileCount: 0);

        Assert.Equal(ComplianceStatus.Unknown, report.Status);
    }

    private ComplianceCheck CheckFootprint(ApplicationFootprint reference, long actualSize, int actualFileCount)
    {
        var profile = CreateProfile() with
        {
            Applications =
            [
                new ApplicationProfile("seven-zip", "7-Zip", ApplicationSourceKind.PackageRelative, "7z.msi", [], reference),
            ],
        };
        var installed = new InstalledSoftwareSnapshot(
            [new InstalledSoftwareEntry("7-Zip 24.09 (x64)", "24.09", reference.InstallLocation, actualSize, actualFileCount)],
            []);

        var report = _checker.Check(profile, CreateMachine(), DateTimeOffset.UnixEpoch, installed);
        return Assert.Single(report.Checks, entry => entry.Title == "Целостность: 7-Zip");
    }

    [Fact]
    public void Check_MissingApplication_ReportsMismatch()
    {
        var profile = CreateProfile() with
        {
            Applications = [new ApplicationProfile("seven-zip", "7-Zip", ApplicationSourceKind.PackageRelative, "7z.msi", [])],
        };
        var installed = new InstalledSoftwareSnapshot([new InstalledSoftwareEntry("Notepad++", "8.6")], []);

        var report = _checker.Check(profile, CreateMachine(), DateTimeOffset.UnixEpoch, installed);

        Assert.False(report.IsCompliant);
        var check = Assert.Single(report.Checks, entry => entry.Title == "Приложение: 7-Zip");
        Assert.Equal(ComplianceStatus.Mismatch, check.Status);
    }

    [Fact]
    public void Check_WithoutInstalledSoftwareReading_ReportsUnknownRatherThanMissing()
    {
        var profile = CreateProfile() with
        {
            Applications = [new ApplicationProfile("seven-zip", "7-Zip", ApplicationSourceKind.PackageRelative, "7z.msi", [])],
        };

        var report = _checker.Check(profile, CreateMachine(), DateTimeOffset.UnixEpoch, installedSoftware: null);

        // An unreadable list must never be reported as "not installed".
        var check = Assert.Single(report.Checks, entry => entry.Title == "Приложения");
        Assert.Equal(ComplianceStatus.Unknown, check.Status);
    }

    [Fact]
    public void Check_DifferentAddress_ReportsMismatch()
    {
        var report = _checker.Check(
            CreateProfile(),
            CreateMachine() with { Address = "192.0.2.99" },
            DateTimeOffset.UnixEpoch);

        Assert.False(report.IsCompliant);
        Assert.Contains(
            report.Checks,
            static check => check.Title == "IPv4-адрес" && check.Status == ComplianceStatus.Mismatch);
    }

    [Fact]
    public void Check_UnreadableValue_IsUnknownRatherThanMismatch()
    {
        var report = _checker.Check(
            CreateProfile(),
            CreateMachine() with { TimeZone = string.Empty },
            DateTimeOffset.UnixEpoch);

        Assert.Contains(
            report.Checks,
            static check => check.Title == "Часовой пояс" && check.Status == ComplianceStatus.Unknown);
        Assert.False(report.IsCompliant);
    }

    [Fact]
    public void Check_ComputerNameKeepsTemplateSuffixFree()
    {
        var profile = CreateProfile();

        var matching = _checker.Check(profile, CreateMachine() with { ComputerName = "SITE01NOMAD07" }, DateTimeOffset.UnixEpoch);
        var wrongPrefix = _checker.Check(profile, CreateMachine() with { ComputerName = "OTHER07" }, DateTimeOffset.UnixEpoch);
        var missingNumber = _checker.Check(profile, CreateMachine() with { ComputerName = "SITE01NOMAD" }, DateTimeOffset.UnixEpoch);

        Assert.Equal(ComplianceStatus.Match, FindCheck(matching, "Имя компьютера").Status);
        Assert.Equal(ComplianceStatus.Mismatch, FindCheck(wrongPrefix, "Имя компьютера").Status);
        Assert.Equal(ComplianceStatus.Mismatch, FindCheck(missingNumber, "Имя компьютера").Status);
    }

    [Fact]
    public void Check_ProfileWithoutStaticNetwork_DoesNotJudgeAddresses()
    {
        var defaultProfile = ProvisioningProfileFactory.CreateDefault();
        var report = _checker.Check(defaultProfile, CreateMachine(), DateTimeOffset.UnixEpoch);

        var network = FindCheck(report, "Сеть");
        Assert.Equal(ComplianceStatus.NotConfigured, network.Status);
        Assert.DoesNotContain(report.Checks, static check => check.Title == "IPv4-адрес");
    }

    [Fact]
    public void Check_OptionalDnsIsNotJudgedWhenProfileLeavesItEmpty()
    {
        var profile = CreateProfile(dnsServers: []);

        var report = _checker.Check(profile, CreateMachine() with { DnsServers = ["9.9.9.9"] }, DateTimeOffset.UnixEpoch);

        Assert.Equal(ComplianceStatus.NotConfigured, FindCheck(report, "DNS-серверы").Status);
    }

    private static ComplianceCheck FindCheck(ComplianceReport report, string title) =>
        report.Checks.Single(check => check.Title == title);

    private static ProvisioningProfile CreateProfile(IReadOnlyList<string>? dnsServers = null)
    {
        var profile = ProvisioningProfileFactory.CreateDefault();
        return profile with
        {
            Windows = profile.Windows with { TimeZone = "UTC" },
            Machine = profile.Machine with
            {
                ComputerName = profile.Machine.ComputerName with { Prefix = "SITE01NOMAD" },
                Network = new NetworkSettings(
                    NetworkConfigurationMode.StaticIpv4,
                    new StaticIpv4Configuration(
                        "192.0.2.77",
                        "255.255.255.0",
                        "192.0.2.254",
                        dnsServers ?? ["192.0.2.53"])),
            },
        };
    }

    private static MachineStateSnapshot CreateMachine() => new(
        "SITE01NOMAD07",
        string.Empty,
        "UTC",
        "Ethernet",
        "192.0.2.77",
        "255.255.255.0",
        "192.0.2.254",
        ["192.0.2.53"],
        string.Empty);
}
