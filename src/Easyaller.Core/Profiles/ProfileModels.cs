using System.Text.Json.Serialization;

namespace Easyaller.Core.Profiles;

public sealed record ProvisioningProfile
{
    public const int CurrentSchemaVersion = 1;

    [JsonRequired]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonRequired]
    public Guid ProfileId { get; init; } = Guid.NewGuid();

    [JsonRequired]
    public int Revision { get; init; } = 1;

    [JsonRequired]
    public required ProfileMetadata Metadata { get; init; }

    [JsonRequired]
    public required WindowsSettings Windows { get; init; }

    [JsonRequired]
    public required MachineSettings Machine { get; init; }

    [JsonRequired]
    public required DomainSettings Domain { get; init; }

    [JsonRequired]
    public IReadOnlyList<ApplicationProfile> Applications { get; init; } = [];

    [JsonRequired]
    public IReadOnlyList<InstructionProfile> Instructions { get; init; } = [];

    [JsonRequired]
    public required DeploymentSettings Deployment { get; init; }

    [JsonRequired]
    public required CleanupSettings Cleanup { get; init; }
}

public sealed record ProfileMetadata([property: JsonRequired] string Name, string? Description);

public sealed record WindowsSettings(
    [property: JsonRequired] IReadOnlyList<WindowsEdition> SupportedEditions,
    [property: JsonRequired] WindowsArchitecture Architecture,
    [property: JsonRequired] LocaleSettings Locale,
    [property: JsonRequired] string TimeZone,
    [property: JsonRequired] OobeSettings Oobe,
    [property: JsonRequired] PrivacySettings Privacy);

public enum WindowsEdition
{
    Professional,
    Enterprise,
}

public enum WindowsArchitecture
{
    Amd64,
}

public sealed record LocaleSettings(
    [property: JsonRequired] string UiLanguage,
    [property: JsonRequired] string InputLocale,
    [property: JsonRequired] string SystemLocale,
    [property: JsonRequired] string UserLocale);

public sealed record OobeSettings(
    bool? HideEula,
    bool? HideWirelessSetup,
    bool? HideOnlineAccountScreens,
    [property: JsonRequired] bool OfflineInitialSetup,
    int? ProtectYourPc);

public sealed record PrivacySettings(
    [property: JsonRequired] PrivacyPreference LocationServices,
    [property: JsonRequired] PrivacyPreference AdvertisingId,
    [property: JsonRequired] PrivacyPreference DiagnosticData,
    [property: JsonRequired] PrivacyPreference TailoredExperiences,
    [property: JsonRequired] PrivacyPreference OnlineSpeechRecognition,
    [property: JsonRequired] PrivacyPreference FindMyDevice,
    [property: JsonRequired] PrivacyPreference InkingAndTypingPersonalization);

public enum PrivacyPreference
{
    NotConfigured,
    UserChoice,
    Enabled,
    Disabled,
}

public sealed record MachineSettings(
    [property: JsonRequired] ComputerNameRule ComputerName,
    [property: JsonRequired] NetworkSettings Network,
    [property: JsonRequired] ProxySettings Proxy);

public sealed record ComputerNameRule([property: JsonRequired] ComputerNameMode Mode, string? Prefix);

public enum ComputerNameMode
{
    RequiredAtRuntime,
}

public sealed record NetworkSettings([property: JsonRequired] NetworkConfigurationMode Mode);

public enum NetworkConfigurationMode
{
    PromptAtRuntime,
}

public sealed record ProxySettings([property: JsonRequired] ProxyConfigurationMode Mode);

public enum ProxyConfigurationMode
{
    NotConfigured,
    PromptAtRuntime,
}

public sealed record DomainSettings(
    [property: JsonRequired] DomainMode Mode,
    [property: JsonRequired] CredentialHandling Credentials);

public enum DomainMode
{
    NotConfigured,
    Optional,
    Required,
}

public enum CredentialHandling
{
    PromptAtRuntime,
}

public sealed record ApplicationProfile(
    [property: JsonRequired] string Id,
    [property: JsonRequired] string DisplayName,
    [property: JsonRequired] ApplicationSourceKind SourceKind,
    string? PackageRelativePath,
    [property: JsonRequired] IReadOnlyList<string> Arguments);

public enum ApplicationSourceKind
{
    PackageRelative,
    ExternalManual,
}

public sealed record InstructionProfile(
    [property: JsonRequired] string Id,
    [property: JsonRequired] string Title,
    [property: JsonRequired] string Content);

public sealed record DeploymentSettings([property: JsonRequired] ProvisionerLaunchMode LaunchMode);

public enum ProvisionerLaunchMode
{
    Manual,
    FirstLogon,
}

public sealed record CleanupSettings([property: JsonRequired] ProvisioningAccountCleanupMode ProvisioningAccount);

public enum ProvisioningAccountCleanupMode
{
    DisableAfterValidation,
    DeleteAfterValidation,
}
