using System.Text.Json;
using Easyaller.Core.Profiles;

namespace Easyaller.App;

public sealed class ProfileEditorController(IProfileRepository repository)
{
    public const string RequiredInputLocales = "en-US;ru-RU";

    private readonly IProfileRepository _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    private readonly ProvisioningProfileValidator _validator = new();

    public ProfileRepositoryWriteResult Save(ProvisioningProfile original, string? name, string? description)
    {
        ArgumentNullException.ThrowIfNull(original);

        var updated = original with
        {
            Metadata = new ProfileMetadata(
                name?.Trim() ?? string.Empty,
                string.IsNullOrWhiteSpace(description) ? null : description.Trim()),
        };
        return _repository.Update(updated, original.Revision);
    }

    public ProfileRepositoryWriteResult SaveSettings(ProvisioningProfile original, ProfileSettingsEdit settings)
        => SaveComplete(original, settings, original.Applications, original.Instructions);

    public ProfileRepositoryWriteResult SaveComplete(
        ProvisioningProfile original,
        ProfileSettingsEdit settings,
        IReadOnlyList<ApplicationProfile> applications,
        IReadOnlyList<InstructionProfile> instructions)
    {
        var updated = CreateCompleteDraft(original, settings, applications, instructions);
        return _repository.Update(updated, original.Revision);
    }

    public ProfileValidationResult ValidateComplete(
        ProvisioningProfile original,
        ProfileSettingsEdit settings,
        IReadOnlyList<ApplicationProfile> applications,
        IReadOnlyList<InstructionProfile> instructions) =>
        _validator.Validate(CreateCompleteDraft(original, settings, applications, instructions));

    public bool HasChanges(
        ProvisioningProfile original,
        ProfileSettingsEdit settings,
        IReadOnlyList<ApplicationProfile> applications,
        IReadOnlyList<InstructionProfile> instructions) =>
        !string.Equals(
            JsonSerializer.Serialize(original),
            JsonSerializer.Serialize(CreateCompleteDraft(original, settings, applications, instructions)),
            StringComparison.Ordinal);

    private static ProvisioningProfile CreateCompleteDraft(
        ProvisioningProfile original,
        ProfileSettingsEdit settings,
        IReadOnlyList<ApplicationProfile> applications,
        IReadOnlyList<InstructionProfile> instructions)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(applications);
        ArgumentNullException.ThrowIfNull(instructions);

        var privacy = settings.PrivacyPreference is { } preference
            ? new PrivacySettings(preference, preference, preference, preference, preference, preference, preference)
            : original.Windows.Privacy;
        return original with
        {
            Metadata = new ProfileMetadata(
                settings.Name?.Trim() ?? string.Empty,
                string.IsNullOrWhiteSpace(settings.Description) ? null : settings.Description.Trim()),
            Windows = original.Windows with
            {
                SupportedEditions = settings.SupportedEditions.ToArray(),
                Locale = new LocaleSettings(
                    settings.UiLanguage?.Trim() ?? string.Empty,
                    RequiredInputLocales,
                    settings.SystemLocale?.Trim() ?? string.Empty,
                    settings.UserLocale?.Trim() ?? string.Empty),
                TimeZone = settings.TimeZone?.Trim() ?? string.Empty,
                Oobe = original.Windows.Oobe with
                {
                    HideWirelessSetup = settings.HideWirelessSetup,
                    HideOnlineAccountScreens = settings.HideOnlineAccountScreens,
                    OfflineInitialSetup = settings.OfflineInitialSetup,
                },
                Privacy = privacy,
            },
            Machine = original.Machine with
            {
                ComputerName = original.Machine.ComputerName with
                {
                    Prefix = string.IsNullOrWhiteSpace(settings.ComputerNamePrefix) ? null : settings.ComputerNamePrefix.Trim(),
                },
                Network = CreateNetworkSettings(settings),
                Proxy = CreateProxySettings(settings),
            },
            Domain = original.Domain with
            {
                // "Do not join" must not keep a stale domain name and account in the profile.
                Mode = settings.DomainMode,
                DomainName = settings.DomainMode == DomainMode.NotConfigured ? null : settings.DomainName?.Trim(),
                UserName = settings.DomainMode == DomainMode.NotConfigured ? null : settings.DomainUserName?.Trim(),
            },
            ApplicationSourcePath = string.IsNullOrWhiteSpace(settings.ApplicationSourcePath)
                ? null
                : settings.ApplicationSourcePath.Trim(),
            Deployment = new DeploymentSettings(settings.LaunchMode),
            Cleanup = new CleanupSettings(settings.CleanupMode),
            Applications = applications.ToArray(),
            Instructions = instructions.ToArray(),
        };
    }

    /// <summary>
    /// The selected mode decides what the profile keeps. Typing an address must never silently
    /// switch the profile to a proxy mode the operator did not choose.
    /// </summary>
    private static ProxySettings CreateProxySettings(ProfileSettingsEdit settings)
    {
        if (settings.ProxyMode != ProxyConfigurationMode.PromptAtRuntime)
        {
            // Keep the empty-list shape the profile format already uses, so an unchanged
            // profile still serializes identically and is not reported as edited.
            return new ProxySettings(settings.ProxyMode, []);
        }

        return new ProxySettings(
            ProxyConfigurationMode.PromptAtRuntime,
            (settings.ProxyBypassList ?? string.Empty)
                .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            settings.ProxyAddress?.Trim());
    }

    private static NetworkSettings CreateNetworkSettings(ProfileSettingsEdit settings) => settings.NetworkMode switch
    {
        NetworkConfigurationMode.PromptAtRuntime => new NetworkSettings(NetworkConfigurationMode.PromptAtRuntime),
        NetworkConfigurationMode.StaticIpv4 => new NetworkSettings(
            NetworkConfigurationMode.StaticIpv4,
            new StaticIpv4Configuration(
                settings.StaticIpv4Address?.Trim() ?? string.Empty,
                settings.StaticIpv4SubnetMask?.Trim() ?? string.Empty,
                settings.StaticIpv4DefaultGateway?.Trim() ?? string.Empty,
                (settings.StaticIpv4DnsServers ?? string.Empty)
                    .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                settings.StaticIpv4AdapterId?.Trim())),
        _ => new NetworkSettings(settings.NetworkMode),
    };
}

public sealed record ProfileSettingsEdit(
    string? Name,
    string? Description,
    IReadOnlyList<WindowsEdition> SupportedEditions,
    string? UiLanguage,
    string? InputLocale,
    string? SystemLocale,
    string? UserLocale,
    string? TimeZone,
    bool OfflineInitialSetup,
    bool? HideWirelessSetup,
    bool? HideOnlineAccountScreens,
    PrivacyPreference? PrivacyPreference,
    string? ComputerNamePrefix,
    ProxyConfigurationMode ProxyMode,
    DomainMode DomainMode,
    ProvisionerLaunchMode LaunchMode,
    ProvisioningAccountCleanupMode CleanupMode,
    NetworkConfigurationMode NetworkMode = NetworkConfigurationMode.PromptAtRuntime,
    string? StaticIpv4Address = null,
    string? StaticIpv4SubnetMask = null,
    string? StaticIpv4DefaultGateway = null,
    string? StaticIpv4DnsServers = null,
    string? StaticIpv4AdapterId = null,
    string? ProxyBypassList = null,
    string? ProxyAddress = null,
    string? DomainName = null,
    string? DomainUserName = null,
    string? ApplicationSourcePath = null);
