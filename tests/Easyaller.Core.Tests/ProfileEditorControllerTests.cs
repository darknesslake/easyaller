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
            ComputerNamePrefix: "LAB",
            ProxyMode: ProxyConfigurationMode.PromptAtRuntime,
            DomainMode: DomainMode.Required,
            LaunchMode: ProvisionerLaunchMode.FirstLogon,
            CleanupMode: ProvisioningAccountCleanupMode.DeleteAfterValidation);

        var result = controller.SaveSettings(original, settings);

        Assert.Equal(ProfileRepositoryStatus.Success, result.Status);
        Assert.Equal(2, result.Profile!.Revision);
        Assert.Equal([WindowsEdition.Enterprise], result.Profile.Windows.SupportedEditions);
        Assert.True(result.Profile.Windows.Oobe.OfflineInitialSetup);
        Assert.True(result.Profile.Windows.Oobe.HideWirelessSetup);
        Assert.True(result.Profile.Windows.Oobe.HideOnlineAccountScreens);
        Assert.Equal("LAB", result.Profile.Machine.ComputerName.Prefix);
        Assert.Equal(DomainMode.Required, result.Profile.Domain.Mode);
        Assert.Equal(ProvisioningAccountCleanupMode.DeleteAfterValidation, result.Profile.Cleanup.ProvisioningAccount);
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
}
