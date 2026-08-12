using Easyaller.App;
using Easyaller.Core.Profiles;

namespace Easyaller.Core.Tests;

public sealed class ProfileEditorControllerTests
{
    [Fact]
    public void Save_ValidEdits_UpdatesTheProfileAndNormalizesDescription()
    {
        using var directory = new TemporaryDirectory();
        var repository = new FileProfileRepository(directory.Path);
        var original = ProvisioningProfileFactory.CreateDefault("Original profile");
        Assert.Equal(ProfileRepositoryStatus.Success, repository.Create(original).Status);
        var controller = new ProfileEditorController(repository);

        var result = controller.Save(original, "  Updated profile  ", "   ");

        Assert.Equal(ProfileRepositoryStatus.Success, result.Status);
        Assert.Equal(2, result.Profile!.Revision);
        Assert.Equal("Updated profile", result.Profile.Metadata.Name);
        Assert.Null(result.Profile.Metadata.Description);
    }

    [Fact]
    public void Save_EmptyName_ReturnsValidationErrorAndPreservesStoredProfile()
    {
        using var directory = new TemporaryDirectory();
        var repository = new FileProfileRepository(directory.Path);
        var original = ProvisioningProfileFactory.CreateDefault("Original profile");
        Assert.Equal(ProfileRepositoryStatus.Success, repository.Create(original).Status);
        var controller = new ProfileEditorController(repository);

        var result = controller.Save(original, "   ", null);

        Assert.Equal(ProfileRepositoryStatus.Invalid, result.Status);
        Assert.Equal("Original profile", repository.Read(original.ProfileId).Profile!.Metadata.Name);
    }

    [Fact]
    public void SaveSettings_ValidWindowsAndMachineSettings_PersistsTheEditedProfile()
    {
        using var directory = new TemporaryDirectory();
        var repository = new FileProfileRepository(directory.Path);
        var original = ProvisioningProfileFactory.CreateDefault("Original profile");
        Assert.Equal(ProfileRepositoryStatus.Success, repository.Create(original).Status);
        var controller = new ProfileEditorController(repository);
        var settings = new ProfileSettingsEdit(
            "Configured profile",
            "Ready for a lab workstation",
            [WindowsEdition.Enterprise],
            "en-US",
            "en-US",
            "en-US",
            "en-US",
            "UTC",
            OfflineInitialSetup: true,
            HideWirelessSetup: true,
            HideOnlineAccountScreens: true,
            PrivacyPreference: PrivacyPreference.Disabled,
            ComputerNamePrefix: "LAB",
            ProxyMode: ProxyConfigurationMode.PromptAtRuntime,
            DomainMode: DomainMode.Required,
            LaunchMode: ProvisionerLaunchMode.FirstLogon,
            CleanupMode: ProvisioningAccountCleanupMode.DeleteAfterValidation);

        var result = controller.SaveSettings(original, settings);

        Assert.Equal(ProfileRepositoryStatus.Success, result.Status);
        Assert.Equal(2, result.Profile!.Revision);
        Assert.Equal([WindowsEdition.Enterprise], result.Profile.Windows.SupportedEditions);
        Assert.Equal(ProfileEditorController.RequiredInputLocales, result.Profile.Windows.Locale.InputLocale);
        Assert.True(result.Profile.Windows.Oobe.OfflineInitialSetup);
        Assert.True(result.Profile.Windows.Oobe.HideWirelessSetup);
        Assert.True(result.Profile.Windows.Oobe.HideOnlineAccountScreens);
        Assert.Equal("LAB", result.Profile.Machine.ComputerName.Prefix);
        Assert.Equal(DomainMode.Required, result.Profile.Domain.Mode);
        Assert.Equal(ProvisioningAccountCleanupMode.DeleteAfterValidation, result.Profile.Cleanup.ProvisioningAccount);
        Assert.Equal(PrivacyPreference.Disabled, result.Profile.Windows.Privacy.LocationServices);
    }

    [Fact]
    public void ValidateComplete_InvalidDraft_ReturnsFieldErrorsWithoutWritingTheProfile()
    {
        using var directory = new TemporaryDirectory();
        var repository = new FileProfileRepository(directory.Path);
        var original = ProvisioningProfileFactory.CreateDefault("Original profile");
        Assert.Equal(ProfileRepositoryStatus.Success, repository.Create(original).Status);
        var controller = new ProfileEditorController(repository);
        var settings = new ProfileSettingsEdit(
            " ",
            original.Metadata.Description,
            [],
            original.Windows.Locale.UiLanguage,
            original.Windows.Locale.InputLocale,
            original.Windows.Locale.SystemLocale,
            original.Windows.Locale.UserLocale,
            original.Windows.TimeZone,
            original.Windows.Oobe.OfflineInitialSetup,
            original.Windows.Oobe.HideWirelessSetup,
            original.Windows.Oobe.HideOnlineAccountScreens,
            null,
            original.Machine.ComputerName.Prefix,
            original.Machine.Proxy.Mode,
            original.Domain.Mode,
            original.Deployment.LaunchMode,
            original.Cleanup.ProvisioningAccount);

        var result = controller.ValidateComplete(original, settings, original.Applications, original.Instructions);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.FieldPath == "metadata.name");
        Assert.Contains(result.Errors, error => error.FieldPath == "windows.supportedEditions");
        Assert.Equal(1, repository.Read(original.ProfileId).Profile!.Revision);
    }

    [Fact]
    public void HasChanges_OriginalValuesAndEditedValues_AreDistinguished()
    {
        using var directory = new TemporaryDirectory();
        var repository = new FileProfileRepository(directory.Path);
        var original = ProvisioningProfileFactory.CreateDefault("Original profile");
        var controller = new ProfileEditorController(repository);
        var originalSettings = CreateSettings(original, original.Metadata.Name);
        var editedSettings = CreateSettings(original, "Edited profile");

        Assert.False(controller.HasChanges(original, originalSettings, original.Applications, original.Instructions));
        Assert.True(controller.HasChanges(original, editedSettings, original.Applications, original.Instructions));
    }

    [Theory]
    [InlineData("original", true)]
    [InlineData("RUSSIAN", true)]
    [InlineData("UTC", true)]
    [InlineData("missing", false)]
    public void ProfileListFilter_NameDescriptionAndDetails_AreSearchable(string query, bool expected)
    {
        var defaultProfile = ProvisioningProfileFactory.CreateDefault("Original profile");
        var profile = defaultProfile with
        {
            Metadata = defaultProfile.Metadata with { Description = "Russian workstation" },
        };

        Assert.Equal(expected, ProfileListFilter.Matches(new ProfileListItem(profile), query));
    }

    [Fact]
    public void SaveComplete_ApplicationsAndInstructions_ArePersistedWithTheProfile()
    {
        using var directory = new TemporaryDirectory();
        var repository = new FileProfileRepository(directory.Path);
        var original = ProvisioningProfileFactory.CreateDefault();
        Assert.Equal(ProfileRepositoryStatus.Success, repository.Create(original).Status);
        var controller = new ProfileEditorController(repository);
        var settings = new ProfileSettingsEdit(
            original.Metadata.Name,
            original.Metadata.Description,
            original.Windows.SupportedEditions,
            original.Windows.Locale.UiLanguage,
            original.Windows.Locale.InputLocale,
            original.Windows.Locale.SystemLocale,
            original.Windows.Locale.UserLocale,
            original.Windows.TimeZone,
            original.Windows.Oobe.OfflineInitialSetup,
            original.Windows.Oobe.HideWirelessSetup,
            original.Windows.Oobe.HideOnlineAccountScreens,
            null,
            original.Machine.ComputerName.Prefix,
            original.Machine.Proxy.Mode,
            original.Domain.Mode,
            original.Deployment.LaunchMode,
            original.Cleanup.ProvisioningAccount);
        var applications = new[]
        {
            new ApplicationProfile("contoso-tool", "Contoso tool", ApplicationSourceKind.PackageRelative, "installers/tool.msi", []),
        };
        var instructions = new[]
        {
            new InstructionProfile("after-install", "After installation", "Open the support portal."),
        };

        var result = controller.SaveComplete(original, settings, applications, instructions);

        Assert.Equal(ProfileRepositoryStatus.Success, result.Status);
        Assert.Equal(applications.Select(static application => application.Id), result.Profile!.Applications.Select(static application => application.Id));
        Assert.Equal(instructions.Select(static instruction => instruction.Id), result.Profile.Instructions.Select(static instruction => instruction.Id));
    }

    [Fact]
    public void SaveSettings_StaticIpv4Settings_PersistsAValidatedNetworkConfiguration()
    {
        using var directory = new TemporaryDirectory();
        var repository = new FileProfileRepository(directory.Path);
        var original = ProvisioningProfileFactory.CreateDefault("Original profile");
        Assert.Equal(ProfileRepositoryStatus.Success, repository.Create(original).Status);
        var controller = new ProfileEditorController(repository);
        var settings = new ProfileSettingsEdit(
            original.Metadata.Name,
            original.Metadata.Description,
            original.Windows.SupportedEditions,
            original.Windows.Locale.UiLanguage,
            original.Windows.Locale.InputLocale,
            original.Windows.Locale.SystemLocale,
            original.Windows.Locale.UserLocale,
            original.Windows.TimeZone,
            original.Windows.Oobe.OfflineInitialSetup,
            original.Windows.Oobe.HideWirelessSetup,
            original.Windows.Oobe.HideOnlineAccountScreens,
            null,
            original.Machine.ComputerName.Prefix,
            original.Machine.Proxy.Mode,
            original.Domain.Mode,
            original.Deployment.LaunchMode,
            original.Cleanup.ProvisioningAccount,
            NetworkMode: NetworkConfigurationMode.StaticIpv4,
            StaticIpv4Address: "192.0.2.77",
            StaticIpv4SubnetMask: "255.255.255.0",
            StaticIpv4DefaultGateway: "192.0.2.254",
            StaticIpv4DnsServers: "192.0.2.53, 198.51.100.53, 203.0.113.53");

        var result = controller.SaveSettings(original, settings);

        Assert.Equal(ProfileRepositoryStatus.Success, result.Status);
        Assert.Equal(NetworkConfigurationMode.StaticIpv4, result.Profile!.Machine.Network.Mode);
        Assert.Equal("192.0.2.77", result.Profile.Machine.Network.StaticIpv4!.Address);
        Assert.Equal(["192.0.2.53", "198.51.100.53", "203.0.113.53"], result.Profile.Machine.Network.StaticIpv4.DnsServers);
    }

    [Fact]
    public void SaveSettings_ProxyBypassList_PersistsTrimmedEntries()
    {
        using var directory = new TemporaryDirectory();
        var repository = new FileProfileRepository(directory.Path);
        var original = ProvisioningProfileFactory.CreateDefault("Original profile");
        Assert.Equal(ProfileRepositoryStatus.Success, repository.Create(original).Status);
        var controller = new ProfileEditorController(repository);
        var settings = new ProfileSettingsEdit(
            original.Metadata.Name,
            original.Metadata.Description,
            original.Windows.SupportedEditions,
            original.Windows.Locale.UiLanguage,
            original.Windows.Locale.InputLocale,
            original.Windows.Locale.SystemLocale,
            original.Windows.Locale.UserLocale,
            original.Windows.TimeZone,
            original.Windows.Oobe.OfflineInitialSetup,
            original.Windows.Oobe.HideWirelessSetup,
            original.Windows.Oobe.HideOnlineAccountScreens,
            null,
            original.Machine.ComputerName.Prefix,
            ProxyConfigurationMode.PromptAtRuntime,
            original.Domain.Mode,
            original.Deployment.LaunchMode,
            original.Cleanup.ProvisioningAccount,
            ProxyBypassList: " *.example.test; <local>\n192.0.2.53 ");

        var result = controller.SaveSettings(original, settings);

        Assert.Equal(ProfileRepositoryStatus.Success, result.Status);
        Assert.Equal(["*.example.test", "<local>", "192.0.2.53"], result.Profile!.Machine.Proxy.BypassList);
    }

    [Fact]
    public void SaveSettings_ProxyAddressWithoutProxyMode_IsNotStoredAndDoesNotChangeTheMode()
    {
        using var directory = new TemporaryDirectory();
        var repository = new FileProfileRepository(directory.Path);
        var original = ProvisioningProfileFactory.CreateDefault("Original profile");
        Assert.Equal(ProfileRepositoryStatus.Success, repository.Create(original).Status);
        var controller = new ProfileEditorController(repository);
        var settings = CreateSettings(original, original.Metadata.Name) with
        {
            ProxyMode = ProxyConfigurationMode.NotConfigured,
            ProxyAddress = "proxy.example.test:8080",
            ProxyBypassList = "*.example.test",
        };

        var result = controller.SaveSettings(original, settings);

        Assert.Equal(ProfileRepositoryStatus.Success, result.Status);
        Assert.Equal(ProxyConfigurationMode.NotConfigured, result.Profile!.Machine.Proxy.Mode);
        Assert.Null(result.Profile.Machine.Proxy.Address);
        Assert.Empty(result.Profile.Machine.Proxy.BypassList!);
    }

    [Fact]
    public void SaveSettings_DomainValuesWithoutDomainJoin_AreNotStored()
    {
        using var directory = new TemporaryDirectory();
        var repository = new FileProfileRepository(directory.Path);
        var original = ProvisioningProfileFactory.CreateDefault("Original profile");
        Assert.Equal(ProfileRepositoryStatus.Success, repository.Create(original).Status);
        var controller = new ProfileEditorController(repository);
        var settings = CreateSettings(original, original.Metadata.Name) with
        {
            DomainMode = DomainMode.NotConfigured,
            DomainName = "corp.example",
            DomainUserName = "operator",
        };

        var result = controller.SaveSettings(original, settings);

        Assert.Equal(ProfileRepositoryStatus.Success, result.Status);
        Assert.Equal(DomainMode.NotConfigured, result.Profile!.Domain.Mode);
        Assert.Null(result.Profile.Domain.DomainName);
        Assert.Null(result.Profile.Domain.UserName);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Easyaller.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private static ProfileSettingsEdit CreateSettings(ProvisioningProfile profile, string name) => new(
        name,
        profile.Metadata.Description,
        profile.Windows.SupportedEditions,
        profile.Windows.Locale.UiLanguage,
        profile.Windows.Locale.InputLocale,
        profile.Windows.Locale.SystemLocale,
        profile.Windows.Locale.UserLocale,
        profile.Windows.TimeZone,
        profile.Windows.Oobe.OfflineInitialSetup,
        profile.Windows.Oobe.HideWirelessSetup,
        profile.Windows.Oobe.HideOnlineAccountScreens,
        null,
        profile.Machine.ComputerName.Prefix,
        profile.Machine.Proxy.Mode,
        profile.Domain.Mode,
        profile.Deployment.LaunchMode,
        profile.Cleanup.ProvisioningAccount,
        profile.Machine.Network.Mode,
        profile.Machine.Network.StaticIpv4?.Address,
        profile.Machine.Network.StaticIpv4?.SubnetMask,
        profile.Machine.Network.StaticIpv4?.DefaultGateway,
        profile.Machine.Network.StaticIpv4 is { } staticIpv4 ? string.Join(", ", staticIpv4.DnsServers) : null,
        string.Join(", ", profile.Machine.Proxy.BypassList ?? []));
}
