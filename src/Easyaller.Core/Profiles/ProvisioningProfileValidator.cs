using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Easyaller.Core.Profiles;

public sealed class ProvisioningProfileValidator
{
    private static readonly Regex ComputerNamePrefixPattern = new(
        "^[A-Za-z0-9-]{1,15}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ProxyBypassEntryPattern = new(
        "^(?:<local>|[A-Za-z0-9*][A-Za-z0-9*._:-]{0,252})$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> KnownCultureNames = CultureInfo
        .GetCultures(CultureTypes.AllCultures)
        .Select(static culture => culture.Name)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public ProfileValidationResult Validate(ProvisioningProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var errors = new List<ProfileValidationError>();

        ValidateIdentity(profile, errors);
        ValidateWindows(profile.Windows, errors);
        ValidateMachine(profile.Machine, errors);
        ValidateDomain(profile.Domain, errors);
        ValidateApplications(profile.Applications, errors);

        return new ProfileValidationResult(errors);
    }

    private static void ValidateIdentity(ProvisioningProfile profile, ICollection<ProfileValidationError> errors)
    {
        if (profile.SchemaVersion != ProvisioningProfile.CurrentSchemaVersion)
        {
            errors.Add(new ProfileValidationError(
                "profile.schemaVersion.unsupported",
                "schemaVersion",
                $"Schema version {profile.SchemaVersion} is not supported."));
        }

        if (profile.ProfileId == Guid.Empty)
        {
            errors.Add(new ProfileValidationError(
                "profile.id.required",
                "profileId",
                "Profile ID is required."));
        }

        if (profile.Revision < 1)
        {
            errors.Add(new ProfileValidationError(
                "profile.revision.invalid",
                "revision",
                "Revision must be at least 1."));
        }

        if (string.IsNullOrWhiteSpace(profile.Metadata.Name))
        {
            errors.Add(new ProfileValidationError(
                "profile.name.required",
                "metadata.name",
                "Profile name is required."));
        }
    }

    private static void ValidateWindows(WindowsSettings windows, ICollection<ProfileValidationError> errors)
    {
        if (windows.SupportedEditions.Count == 0)
        {
            errors.Add(new ProfileValidationError(
                "windows.editions.required",
                "windows.supportedEditions",
                "At least one supported Windows edition is required."));
        }

        if (windows.SupportedEditions.Distinct().Count() != windows.SupportedEditions.Count)
        {
            errors.Add(new ProfileValidationError(
                "windows.editions.duplicate",
                "windows.supportedEditions",
                "Each supported Windows edition can appear only once."));
        }

        if (windows.Architecture != WindowsArchitecture.Amd64)
        {
            errors.Add(new ProfileValidationError(
                "windows.architecture.unsupported",
                "windows.architecture",
                "Only amd64 is supported."));
        }

        ValidateLocale(windows.Locale, errors);

        if (string.IsNullOrWhiteSpace(windows.TimeZone))
        {
            errors.Add(new ProfileValidationError(
                "windows.timeZone.required",
                "windows.timeZone",
                "Windows time zone is required."));
        }

        if (windows.Oobe.ProtectYourPc is not null and (< 1 or > 3))
        {
            errors.Add(new ProfileValidationError(
                "windows.oobe.protectYourPc.invalid",
                "windows.oobe.protectYourPc",
                "ProtectYourPc must be between 1 and 3 when configured."));
        }

        if (windows.Oobe.OfflineInitialSetup && windows.Oobe.HideWirelessSetup != true)
        {
            errors.Add(new ProfileValidationError(
                "windows.oobe.offline.requiresWirelessHide",
                "windows.oobe.hideWirelessSetup",
                "Offline initial setup requires HideWirelessSetup to be true."));
        }

        if (windows.Oobe.OfflineInitialSetup && windows.Oobe.HideOnlineAccountScreens != true)
        {
            errors.Add(new ProfileValidationError(
                "windows.oobe.offline.requiresOnlineAccountHide",
                "windows.oobe.hideOnlineAccountScreens",
                "Offline initial setup requires HideOnlineAccountScreens to be true."));
        }
    }

    private static void ValidateLocale(LocaleSettings locale, ICollection<ProfileValidationError> errors)
    {
        ValidateCulture(locale.UiLanguage, "windows.locale.uiLanguage", errors);
        ValidateInputLocales(locale.InputLocale, errors);
        ValidateCulture(locale.SystemLocale, "windows.locale.systemLocale", errors);
        ValidateCulture(locale.UserLocale, "windows.locale.userLocale", errors);
    }

    private static void ValidateInputLocales(string inputLocales, ICollection<ProfileValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(inputLocales))
        {
            errors.Add(new ProfileValidationError(
                "windows.locale.required",
                "windows.locale.inputLocale",
                "Locale is required."));
            return;
        }

        var values = inputLocales.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (values.Length == 0)
        {
            errors.Add(new ProfileValidationError(
                "windows.locale.required",
                "windows.locale.inputLocale",
                "Locale is required."));
            return;
        }

        foreach (var inputLocale in values)
        {
            ValidateCulture(inputLocale, "windows.locale.inputLocale", errors);
        }
    }

    private static void ValidateCulture(string cultureName, string fieldPath, ICollection<ProfileValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(cultureName))
        {
            errors.Add(new ProfileValidationError(
                "windows.locale.required",
                fieldPath,
                "Locale is required."));
            return;
        }

        if (!KnownCultureNames.Contains(cultureName))
        {
            errors.Add(new ProfileValidationError(
                "windows.locale.unknown",
                fieldPath,
                $"Locale '{cultureName}' is not recognized."));
        }
    }

    private static void ValidateMachine(MachineSettings machine, ICollection<ProfileValidationError> errors)
    {
        if (machine.ComputerName.Mode != ComputerNameMode.RequiredAtRuntime)
        {
            errors.Add(new ProfileValidationError(
                "machine.computerName.mode.invalid",
                "machine.computerName.mode",
                "Computer name must be required at runtime."));
        }

        if (machine.ComputerName.Prefix is { Length: > 0 } prefix && !ComputerNamePrefixPattern.IsMatch(prefix))
        {
            errors.Add(new ProfileValidationError(
                "machine.computerName.prefix.invalid",
                "machine.computerName.prefix",
                "Computer name prefix must contain 1 to 15 letters, numbers, or hyphens."));
        }

        switch (machine.Network.Mode)
        {
            case NetworkConfigurationMode.PromptAtRuntime when machine.Network.StaticIpv4 is not null:
                errors.Add(new ProfileValidationError(
                    "machine.network.staticIpv4.unexpected",
                    "machine.network.staticIpv4",
                    "Static IPv4 settings require staticIpv4 network mode."));
                break;

            case NetworkConfigurationMode.StaticIpv4 when machine.Network.StaticIpv4 is null:
                errors.Add(new ProfileValidationError(
                    "machine.network.staticIpv4.required",
                    "machine.network.staticIpv4",
                    "Static IPv4 mode requires an IPv4 address, subnet mask, gateway, and DNS servers."));
                break;

            case NetworkConfigurationMode.StaticIpv4:
                ValidateStaticIpv4(machine.Network.StaticIpv4!, errors);
                break;

            case not NetworkConfigurationMode.PromptAtRuntime:
                errors.Add(new ProfileValidationError(
                    "machine.network.mode.invalid",
                    "machine.network.mode",
                    "Network configuration mode is not supported."));
                break;
        }

        ValidateProxy(machine.Proxy, errors);
    }

    private static void ValidateProxy(ProxySettings proxy, ICollection<ProfileValidationError> errors)
    {
        if (proxy.Mode is not (ProxyConfigurationMode.NotConfigured or ProxyConfigurationMode.PromptAtRuntime))
        {
            errors.Add(new ProfileValidationError(
                "machine.proxy.mode.invalid",
                "machine.proxy.mode",
                "Proxy configuration mode is not supported."));
        }

        if (proxy.BypassList is null || proxy.BypassList.Count == 0)
        {
            return;
        }

        if (proxy.Mode != ProxyConfigurationMode.PromptAtRuntime)
        {
            errors.Add(new ProfileValidationError(
                "machine.proxy.bypassList.unexpected",
                "machine.proxy.bypassList",
                "A proxy bypass list requires promptAtRuntime proxy mode."));
            return;
        }

        if (proxy.BypassList.Count > 256)
        {
            errors.Add(new ProfileValidationError(
                "machine.proxy.bypassList.count.invalid",
                "machine.proxy.bypassList",
                "Proxy bypass list can contain at most 256 entries."));
        }

        var entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < proxy.BypassList.Count; index++)
        {
            var entry = proxy.BypassList[index];
            var fieldPath = $"machine.proxy.bypassList[{index}]";
            if (string.IsNullOrWhiteSpace(entry) || entry != entry.Trim() || !ProxyBypassEntryPattern.IsMatch(entry))
            {
                errors.Add(new ProfileValidationError(
                    "machine.proxy.bypassList.entry.invalid",
                    fieldPath,
                    "Proxy bypass entries must be host, IPv4, wildcard, or <local> values without spaces or commands."));
                continue;
            }

            if (!entries.Add(entry))
            {
                errors.Add(new ProfileValidationError(
                    "machine.proxy.bypassList.entry.duplicate",
                    fieldPath,
                    "Proxy bypass entries must be unique."));
            }
        }
    }

    private static void ValidateDomain(DomainSettings domain, ICollection<ProfileValidationError> errors)
    {
        if (domain.Credentials != CredentialHandling.PromptAtRuntime)
        {
            errors.Add(new ProfileValidationError(
                "domain.credentials.forbidden",
                "domain.credentials",
                "Domain credentials must be prompted at runtime."));
        }
    }

    private static void ValidateApplications(
        IReadOnlyList<ApplicationProfile> applications,
        ICollection<ProfileValidationError> errors)
    {
        foreach (var application in applications)
        {
            var fieldPrefix = $"applications[{application.Id}]";

            if (string.IsNullOrWhiteSpace(application.Id))
            {
                errors.Add(new ProfileValidationError(
                    "applications.id.required",
                    "applications",
                    "Application ID is required."));
            }

            if (string.IsNullOrWhiteSpace(application.DisplayName))
            {
                errors.Add(new ProfileValidationError(
                    "applications.displayName.required",
                    $"{fieldPrefix}.displayName",
                    "Application display name is required."));
            }

            if (application.SourceKind == ApplicationSourceKind.PackageRelative)
            {
                ValidatePackageRelativePath(application.PackageRelativePath, $"{fieldPrefix}.packageRelativePath", errors);
            }

            if (application.SourceKind == ApplicationSourceKind.ExternalManual && application.PackageRelativePath is not null)
            {
                errors.Add(new ProfileValidationError(
                    "applications.externalManual.path.forbidden",
                    $"{fieldPrefix}.packageRelativePath",
                    "External manual applications cannot declare a package path."));
            }

            if (application.Arguments.Any(static argument => string.IsNullOrWhiteSpace(argument) || argument.Contains('\r') || argument.Contains('\n')))
            {
                errors.Add(new ProfileValidationError(
                    "applications.arguments.invalid",
                    $"{fieldPrefix}.arguments",
                    "Application arguments must be non-empty single-line values."));
            }
        }
    }

    private static void ValidateStaticIpv4(StaticIpv4Configuration configuration, ICollection<ProfileValidationError> errors)
    {
        var address = ParseUsableIpv4(configuration.Address, "machine.network.staticIpv4.address", errors);
        var subnetMask = ParseIpv4(configuration.SubnetMask, "machine.network.staticIpv4.subnetMask", errors);
        var gateway = ParseUsableIpv4(configuration.DefaultGateway, "machine.network.staticIpv4.defaultGateway", errors);

        var prefixLength = subnetMask is null ? null : GetPrefixLength(subnetMask);
        if (subnetMask is not null && prefixLength is null)
        {
            errors.Add(new ProfileValidationError(
                "machine.network.staticIpv4.subnetMask.invalid",
                "machine.network.staticIpv4.subnetMask",
                "Subnet mask must contain contiguous one bits followed by zero bits."));
        }

        if (prefixLength is 0 or 31 or 32)
        {
            errors.Add(new ProfileValidationError(
                "machine.network.staticIpv4.subnetMask.unsupported",
                "machine.network.staticIpv4.subnetMask",
                "Subnet mask must describe a usable IPv4 host range."));
        }

        if (address is not null && gateway is not null && prefixLength is > 0 and < 31)
        {
            var mask = ToUInt32(subnetMask!);
            var addressValue = ToUInt32(address);
            var gatewayValue = ToUInt32(gateway);
            var network = addressValue & mask;
            var broadcast = network | ~mask;
            if ((gatewayValue & mask) != network)
            {
                errors.Add(new ProfileValidationError(
                    "machine.network.staticIpv4.gateway.outsideSubnet",
                    "machine.network.staticIpv4.defaultGateway",
                    "Default gateway must be in the same IPv4 subnet as the address."));
            }

            if (addressValue == network || addressValue == broadcast)
            {
                errors.Add(new ProfileValidationError(
                    "machine.network.staticIpv4.address.host.invalid",
                    "machine.network.staticIpv4.address",
                    "IPv4 address cannot be the network or broadcast address."));
            }

            if (gatewayValue == network || gatewayValue == broadcast || gatewayValue == addressValue)
            {
                errors.Add(new ProfileValidationError(
                    "machine.network.staticIpv4.gateway.host.invalid",
                    "machine.network.staticIpv4.defaultGateway",
                    "Default gateway must be a different usable IPv4 host address."));
            }
        }

        if (configuration.DnsServers is null || configuration.DnsServers.Count is < 1 or > 3)
        {
            errors.Add(new ProfileValidationError(
                "machine.network.staticIpv4.dnsServers.count.invalid",
                "machine.network.staticIpv4.dnsServers",
                "Provide between one and three DNS server addresses."));
            return;
        }

        var normalizedDns = new HashSet<IPAddress>();
        for (var index = 0; index < configuration.DnsServers.Count; index++)
        {
            var dns = ParseUsableIpv4(configuration.DnsServers[index], $"machine.network.staticIpv4.dnsServers[{index}]", errors);
            if (dns is not null && !normalizedDns.Add(dns))
            {
                errors.Add(new ProfileValidationError(
                    "machine.network.staticIpv4.dnsServers.duplicate",
                    $"machine.network.staticIpv4.dnsServers[{index}]",
                    "DNS server addresses must be unique."));
            }
        }
    }

    public static int? GetPrefixLength(string subnetMask)
    {
        if (!IPAddress.TryParse(subnetMask, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }

        return GetPrefixLength(address);
    }

    private static IPAddress? ParseIpv4(string? value, string fieldPath, ICollection<ProfileValidationError> errors)
    {
        if (IPAddress.TryParse(value, out var address) && address.AddressFamily == AddressFamily.InterNetwork)
        {
            return address;
        }

        errors.Add(new ProfileValidationError(
            "machine.network.staticIpv4.ipv4.invalid",
            fieldPath,
            "Value must be a valid IPv4 address."));
        return null;
    }

    private static IPAddress? ParseUsableIpv4(string? value, string fieldPath, ICollection<ProfileValidationError> errors)
    {
        var address = ParseIpv4(value, fieldPath, errors);
        if (address is null)
        {
            return null;
        }

        var numericAddress = ToUInt32(address);
        if (IPAddress.IsLoopback(address) || numericAddress == 0 || numericAddress >= 0xE0000000)
        {
            errors.Add(new ProfileValidationError(
                "machine.network.staticIpv4.ipv4.unusable",
                fieldPath,
                "IPv4 address cannot be unspecified, loopback, multicast, or reserved."));
            return null;
        }

        return address;
    }

    private static int? GetPrefixLength(IPAddress subnetMask)
    {
        var prefixLength = 0;
        var encounteredZero = false;
        foreach (var octet in subnetMask.GetAddressBytes())
        {
            for (var bit = 7; bit >= 0; bit--)
            {
                var isSet = (octet & (1 << bit)) != 0;
                if (!isSet)
                {
                    encounteredZero = true;
                }
                else if (encounteredZero)
                {
                    return null;
                }
                else
                {
                    prefixLength++;
                }
            }
        }

        return prefixLength;
    }

    private static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static void ValidatePackageRelativePath(
        string? path,
        string fieldPath,
        ICollection<ProfileValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            errors.Add(new ProfileValidationError(
                "applications.packagePath.required",
                fieldPath,
                "A package-relative path is required."));
            return;
        }

        if (Path.IsPathRooted(path) || path.Split(['/', '\\']).Any(static segment => segment is "." or ".."))
        {
            errors.Add(new ProfileValidationError(
                "applications.packagePath.unsafe",
                fieldPath,
                "Application path must stay inside the deployment package."));
        }
    }
}

public sealed record ProfileValidationResult(IReadOnlyList<ProfileValidationError> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

public sealed record ProfileValidationError(string Code, string FieldPath, string Message);
