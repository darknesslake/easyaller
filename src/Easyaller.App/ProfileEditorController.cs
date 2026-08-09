using Easyaller.Core.Profiles;

namespace Easyaller.App;

public sealed class ProfileEditorController(IProfileRepository repository)
{
    private readonly IProfileRepository _repository = repository ?? throw new ArgumentNullException(nameof(repository));

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
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(settings);

        ArgumentNullException.ThrowIfNull(applications);
        ArgumentNullException.ThrowIfNull(instructions);

        var privacy = settings.PrivacyPreference is { } preference
            ? new PrivacySettings(preference, preference, preference, preference, preference, preference, preference)
            : original.Windows.Privacy;
        var updated = original with
        {
            Metadata = new ProfileMetadata(
                settings.Name?.Trim() ?? string.Empty,
                string.IsNullOrWhiteSpace(settings.Description) ? null : settings.Description.Trim()),
            Windows = original.Windows with
            {
                SupportedEditions = settings.SupportedEditions.ToArray(),
                Locale = new LocaleSettings(
                    settings.UiLanguage?.Trim() ?? string.Empty,
                    settings.InputLocale?.Trim() ?? string.Empty,
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
                Proxy = new ProxySettings(settings.ProxyMode),
            },
            Domain = original.Domain with { Mode = settings.DomainMode },
            Deployment = new DeploymentSettings(settings.LaunchMode),
            Cleanup = new CleanupSettings(settings.CleanupMode),
            Applications = applications.ToArray(),
            Instructions = instructions.ToArray(),
        };
        return _repository.Update(updated, original.Revision);
    }
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
    ProvisioningAccountCleanupMode CleanupMode);
