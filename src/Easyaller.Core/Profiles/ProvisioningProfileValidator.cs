using System.Globalization;
using System.Text.RegularExpressions;

namespace Easyaller.Core.Profiles;

public sealed class ProvisioningProfileValidator
{
    private static readonly Regex ComputerNamePrefixPattern = new(
        "^[A-Za-z0-9-]{1,15}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

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
        ValidateCulture(locale.InputLocale, "windows.locale.inputLocale", errors);
        ValidateCulture(locale.SystemLocale, "windows.locale.systemLocale", errors);
        ValidateCulture(locale.UserLocale, "windows.locale.userLocale", errors);
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

        if (machine.Network.Mode != NetworkConfigurationMode.PromptAtRuntime)
        {
            errors.Add(new ProfileValidationError(
                "machine.network.mode.invalid",
                "machine.network.mode",
                "Network configuration must be prompted at runtime."));
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
