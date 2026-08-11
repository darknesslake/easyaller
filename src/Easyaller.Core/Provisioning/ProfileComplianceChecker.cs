using Easyaller.Core.Profiles;

namespace Easyaller.Core.Provisioning;

public enum ComplianceStatus
{
    /// <summary>The profile does not describe this setting, so nothing is expected.</summary>
    NotConfigured,

    /// <summary>The machine matches what the profile describes.</summary>
    Match,

    /// <summary>The machine differs from the profile.</summary>
    Mismatch,

    /// <summary>The current value could not be read, so no verdict is possible.</summary>
    Unknown,
}

public sealed record ComplianceCheck(
    string Title,
    ComplianceStatus Status,
    string Expected,
    string Actual);

public sealed record ComplianceReport(
    Guid ProfileId,
    int ProfileRevision,
    string ProfileName,
    DateTimeOffset CheckedUtc,
    IReadOnlyList<ComplianceCheck> Checks)
{
    public int MatchCount => Checks.Count(static check => check.Status == ComplianceStatus.Match);

    public int MismatchCount => Checks.Count(static check => check.Status == ComplianceStatus.Mismatch);

    public int UnknownCount => Checks.Count(static check => check.Status == ComplianceStatus.Unknown);

    public bool IsCompliant => MismatchCount == 0 && UnknownCount == 0 && MatchCount > 0;
}

/// <summary>
/// A read-only snapshot of the values a running Windows installation reports.
/// Every field may be empty when the value could not be read.
/// </summary>
public sealed record MachineStateSnapshot(
    string ComputerName,
    string Domain,
    string TimeZone,
    string AdapterId,
    string Address,
    string SubnetMask,
    string DefaultGateway,
    IReadOnlyList<string> DnsServers,
    string ProxyAddress);

/// <summary>
/// Compares a running machine with the profile that is supposed to describe it.
/// This is a pure comparison: it reads nothing and changes nothing.
/// </summary>
public sealed class ProfileComplianceChecker
{
    public ComplianceReport Check(ProvisioningProfile profile, MachineStateSnapshot machine, DateTimeOffset checkedUtc)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(machine);

        var checks = new List<ComplianceCheck>
        {
            CheckComputerName(profile, machine),
            CheckTimeZone(profile, machine),
        };

        checks.AddRange(CheckNetwork(profile, machine));
        checks.Add(CheckProxy(profile, machine));
        checks.Add(CheckDomain(profile, machine));

        return new ComplianceReport(
            profile.ProfileId,
            profile.Revision,
            profile.Metadata.Name,
            checkedUtc,
            checks);
    }

    private static ComplianceCheck CheckComputerName(ProvisioningProfile profile, MachineStateSnapshot machine)
    {
        var prefix = profile.Machine.ComputerName.Prefix;
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return new ComplianceCheck("Имя компьютера", ComplianceStatus.NotConfigured, "шаблон не задан", machine.ComputerName);
        }

        if (string.IsNullOrWhiteSpace(machine.ComputerName))
        {
            return new ComplianceCheck("Имя компьютера", ComplianceStatus.Unknown, $"начинается с {prefix}", "не прочитано");
        }

        // The profile stores only the prefix; the trailing number is chosen per machine.
        var matches = machine.ComputerName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && machine.ComputerName.Length > prefix.Length
            && machine.ComputerName[prefix.Length..].All(char.IsAsciiDigit);

        return new ComplianceCheck(
            "Имя компьютера",
            matches ? ComplianceStatus.Match : ComplianceStatus.Mismatch,
            $"{prefix} + 2–3 цифры",
            machine.ComputerName);
    }

    private static ComplianceCheck CheckTimeZone(ProvisioningProfile profile, MachineStateSnapshot machine)
    {
        var expected = profile.Windows.TimeZone;
        if (string.IsNullOrWhiteSpace(expected))
        {
            return new ComplianceCheck("Часовой пояс", ComplianceStatus.NotConfigured, "не задан", machine.TimeZone);
        }

        if (string.IsNullOrWhiteSpace(machine.TimeZone))
        {
            return new ComplianceCheck("Часовой пояс", ComplianceStatus.Unknown, expected, "не прочитан");
        }

        return new ComplianceCheck(
            "Часовой пояс",
            string.Equals(expected, machine.TimeZone, StringComparison.OrdinalIgnoreCase)
                ? ComplianceStatus.Match
                : ComplianceStatus.Mismatch,
            expected,
            machine.TimeZone);
    }

    private static IEnumerable<ComplianceCheck> CheckNetwork(ProvisioningProfile profile, MachineStateSnapshot machine)
    {
        if (profile.Machine.Network.Mode != NetworkConfigurationMode.StaticIpv4 ||
            profile.Machine.Network.StaticIpv4 is not { } expected)
        {
            yield return new ComplianceCheck(
                "Сеть",
                ComplianceStatus.NotConfigured,
                "адреса выбираются при применении",
                DescribeAddress(machine));
            yield break;
        }

        yield return CompareText("IPv4-адрес", expected.Address, machine.Address);
        yield return CompareText("Маска подсети", expected.SubnetMask, machine.SubnetMask);

        yield return string.IsNullOrWhiteSpace(expected.DefaultGateway)
            ? new ComplianceCheck("Шлюз по умолчанию", ComplianceStatus.NotConfigured, "не задан", Or(machine.DefaultGateway, "нет"))
            : CompareText("Шлюз по умолчанию", expected.DefaultGateway, machine.DefaultGateway);

        if (expected.DnsServers.Count == 0)
        {
            yield return new ComplianceCheck(
                "DNS-серверы",
                ComplianceStatus.NotConfigured,
                "не задаются профилем",
                Or(string.Join(", ", machine.DnsServers), "нет"));
            yield break;
        }

        // Order matters to Windows resolution, so the comparison keeps it.
        var actualDns = string.Join(", ", machine.DnsServers);
        var expectedDns = string.Join(", ", expected.DnsServers);
        yield return new ComplianceCheck(
            "DNS-серверы",
            machine.DnsServers.Count == 0
                ? ComplianceStatus.Unknown
                : string.Equals(expectedDns, actualDns, StringComparison.OrdinalIgnoreCase)
                    ? ComplianceStatus.Match
                    : ComplianceStatus.Mismatch,
            expectedDns,
            Or(actualDns, "не прочитаны"));
    }

    private static ComplianceCheck CheckProxy(ProvisioningProfile profile, MachineStateSnapshot machine)
    {
        var expected = profile.Machine.Proxy.Address;
        // An address saved in a profile is an explicit proxy configuration even
        // when an older profile still carries the legacy NotConfigured mode.
        if (string.IsNullOrWhiteSpace(expected))
        {
            return new ComplianceCheck(
                "WinHTTP-прокси (не общий прокси Windows)",
                ComplianceStatus.NotConfigured,
                "не настраивается профилем",
                Or(machine.ProxyAddress, "нет"));
        }

        return new ComplianceCheck(
            "WinHTTP-прокси",
            string.IsNullOrWhiteSpace(machine.ProxyAddress)
                ? ComplianceStatus.Mismatch
                : string.Equals(expected, machine.ProxyAddress, StringComparison.OrdinalIgnoreCase)
                    ? ComplianceStatus.Match
                    : ComplianceStatus.Mismatch,
            expected,
            Or(machine.ProxyAddress, "нет"));
    }

    private static ComplianceCheck CheckDomain(ProvisioningProfile profile, MachineStateSnapshot machine)
    {
        var expected = profile.Domain.DomainName;
        if (profile.Domain.Mode == DomainMode.NotConfigured || string.IsNullOrWhiteSpace(expected))
        {
            return new ComplianceCheck("Домен", ComplianceStatus.NotConfigured, "присоединение не требуется", Or(machine.Domain, "рабочая группа"));
        }

        return new ComplianceCheck(
            "Домен",
            string.Equals(expected, machine.Domain, StringComparison.OrdinalIgnoreCase)
                ? ComplianceStatus.Match
                : ComplianceStatus.Mismatch,
            expected,
            Or(machine.Domain, "рабочая группа"));
    }

    private static ComplianceCheck CompareText(string title, string expected, string actual) =>
        new(
            title,
            string.IsNullOrWhiteSpace(actual)
                ? ComplianceStatus.Unknown
                : string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)
                    ? ComplianceStatus.Match
                    : ComplianceStatus.Mismatch,
            expected,
            Or(actual, "не прочитано"));

    private static string DescribeAddress(MachineStateSnapshot machine) =>
        string.IsNullOrWhiteSpace(machine.Address) ? "не прочитано" : $"{machine.Address}/{machine.SubnetMask}";

    private static string Or(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;
}
