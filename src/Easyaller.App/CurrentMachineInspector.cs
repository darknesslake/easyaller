using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Easyaller.Core.Provisioning;

namespace Easyaller.App;

/// <summary>
/// Reads the live Windows configuration so an operator can copy it into a profile.
/// Every operation is read-only, needs no administrator rights, and never changes Windows.
/// </summary>
public sealed class CurrentMachineInspector
{
    private const string ReadConfigurationScript = """
        $ErrorActionPreference = 'SilentlyContinue'
        $result = [ordered]@{
            computerName = $env:COMPUTERNAME
            domain = ''
            timeZone = ''
            adapterId = ''
            address = ''
            prefixLength = 0
            defaultGateway = ''
            dnsServers = @()
            proxyAddress = ''
            proxyBypassList = ''
        }

        $system = Get-CimInstance -ClassName Win32_ComputerSystem
        if ($system -and $system.PartOfDomain) { $result.domain = $system.Domain }

        $timeZone = Get-TimeZone
        if ($timeZone) { $result.timeZone = $timeZone.Id }

        $configurations = @(Get-NetIPConfiguration | Where-Object { $_.NetAdapter.Status -eq 'Up' })
        $configuration = $configurations | Where-Object { $_.IPv4DefaultGateway } | Select-Object -First 1
        if (-not $configuration) { $configuration = $configurations | Where-Object { $_.IPv4Address } | Select-Object -First 1 }

        if ($configuration) {
            $result.adapterId = $configuration.InterfaceAlias
            $ipv4 = @($configuration.IPv4Address) | Select-Object -First 1
            if ($ipv4) {
                $result.address = $ipv4.IPAddress
                $result.prefixLength = [int]$ipv4.PrefixLength
            }
            $gateway = @($configuration.IPv4DefaultGateway) | Select-Object -First 1
            if ($gateway) { $result.defaultGateway = $gateway.NextHop }
            $result.dnsServers = @(
                @($configuration.DnsServer | Where-Object { $_.AddressFamily -eq 2 } | ForEach-Object { $_.ServerAddresses }) |
                    Where-Object { $_ } | Select-Object -Unique -First 3)
        }

        # WinINET: the proxy shown in Параметры > Сеть > Прокси, used by the browser and most apps.
        $inetSettings = Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings' -ErrorAction SilentlyContinue
        if ($inetSettings -and $inetSettings.ProxyEnable -eq 1 -and $inetSettings.ProxyServer) {
            $result.proxyAddress = $inetSettings.ProxyServer
            if ($inetSettings.ProxyOverride) { $result.proxyBypassList = $inetSettings.ProxyOverride }
        }

        $result | ConvertTo-Json -Compress -Depth 4
        """;

    public CurrentMachineSnapshot? Read() => Parse(RunPowerShell(ReadConfigurationScript));

    /// <summary>
    /// Turns the query output into a snapshot. Kept separate from the process call so the
    /// localized <c>netsh</c> text and PowerShell's JSON quirks can be covered by tests.
    /// </summary>
    public static CurrentMachineSnapshot? Parse(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            var prefixLength = GetInt32(root, "prefixLength");
            return new CurrentMachineSnapshot(
                GetString(root, "computerName"),
                GetString(root, "domain"),
                GetString(root, "timeZone"),
                GetString(root, "adapterId"),
                GetString(root, "address"),
                ToSubnetMask(prefixLength),
                GetString(root, "defaultGateway"),
                GetStringArray(root, "dnsServers"),
                GetString(root, "proxyAddress"),
                GetString(root, "proxyBypassList"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ToSubnetMask(int prefixLength)
    {
        if (prefixLength is < 1 or > 32)
        {
            return string.Empty;
        }

        var mask = prefixLength == 32 ? uint.MaxValue : ~(uint.MaxValue >> prefixLength);
        return new IPAddress(
        [
            (byte)(mask >> 24),
            (byte)(mask >> 16),
            (byte)(mask >> 8),
            (byte)mask,
        ]).ToString();
    }

    private static string GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int GetInt32(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;

    private static IReadOnlyList<string> GetStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return [];
        }

        // ConvertTo-Json collapses a single-element array into a bare value.
        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() is { Length: > 0 } single ? [single] : [];
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString()!)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
    }

    private static string RunPowerShell(string script)
    {
        if (!OperatingSystem.IsWindows())
        {
            return string.Empty;
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                },
            };
            process.StartInfo.ArgumentList.Add("-NoLogo");
            process.StartInfo.ArgumentList.Add("-NoProfile");
            process.StartInfo.ArgumentList.Add("-NonInteractive");
            process.StartInfo.ArgumentList.Add("-Command");
            process.StartInfo.ArgumentList.Add(script);

            if (!process.Start())
            {
                return string.Empty;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? output : string.Empty;
        }
        catch (Win32Exception)
        {
            return string.Empty;
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }
}

public sealed record CurrentMachineSnapshot(
    string ComputerName,
    string Domain,
    string TimeZone,
    string AdapterId,
    string Address,
    string SubnetMask,
    string DefaultGateway,
    IReadOnlyList<string> DnsServers,
    string ProxyAddress,
    string ProxyBypassList)
{
    public bool HasNetwork => !string.IsNullOrWhiteSpace(Address) && !string.IsNullOrWhiteSpace(SubnetMask);

    public MachineStateSnapshot ToMachineState() => new(
        ComputerName,
        Domain,
        TimeZone,
        AdapterId,
        Address,
        SubnetMask,
        DefaultGateway,
        DnsServers,
        ProxyAddress);

    public string DescribeNetwork() => HasNetwork
        ? $"{AdapterId}: {Address}/{SubnetMask}"
            + (string.IsNullOrWhiteSpace(DefaultGateway) ? ", без шлюза" : $", шлюз {DefaultGateway}")
            + (DnsServers.Count == 0 ? ", без DNS" : $", DNS: {string.Join(", ", DnsServers)}")
        : "активный адаптер с IPv4 не найден";
}
