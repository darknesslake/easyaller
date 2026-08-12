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

    /// <summary>
    /// Optional network folder that holds the installers. It is confidential but not secret:
    /// it names an internal share, so export review shows it.
    /// </summary>
    public string? ApplicationSourcePath { get; init; }

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

public sealed record NetworkSettings(
    [property: JsonRequired] NetworkConfigurationMode Mode,
    StaticIpv4Configuration? StaticIpv4 = null);

public enum NetworkConfigurationMode
{
    PromptAtRuntime,
    StaticIpv4,
}

public sealed record StaticIpv4Configuration(
    [property: JsonRequired] string Address,
    [property: JsonRequired] string SubnetMask,
    [property: JsonRequired] string DefaultGateway,
    [property: JsonRequired] IReadOnlyList<string> DnsServers,
    string? AdapterId = null);

public sealed record ProxySettings(
    [property: JsonRequired] ProxyConfigurationMode Mode,
    IReadOnlyList<string>? BypassList = null,
    string? Address = null);

public enum ProxyConfigurationMode
{
    NotConfigured,
    PromptAtRuntime,
}

public sealed record DomainSettings(
    [property: JsonRequired] DomainMode Mode,
    [property: JsonRequired] CredentialHandling Credentials,
    string? DomainName = null,
    string? UserName = null);

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
    [property: JsonRequired] IReadOnlyList<string> Arguments,
    ApplicationFootprint? Footprint = null,
    ApplicationArchitecture Architecture = ApplicationArchitecture.Any);

/// <summary>
/// Which Windows architecture an installer is meant for. A profile can therefore carry both the
/// 64-bit and the 32-bit build of the same product, and only the matching one is installed.
/// </summary>
public enum ApplicationArchitecture
{
    Any,
    X64,
    X86,
}

/// <summary>
/// What a correct installation of this application looked like on a reference machine.
/// It is a health indicator, not a cryptographic guarantee: it detects a folder that lost files,
/// which is the realistic failure, and deliberately tolerates growth from caches and updates.
/// </summary>
public sealed record ApplicationFootprint(
    [property: JsonRequired] string InstallLocation,
    [property: JsonRequired] long SizeBytes,
    [property: JsonRequired] int FileCount);

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
