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
