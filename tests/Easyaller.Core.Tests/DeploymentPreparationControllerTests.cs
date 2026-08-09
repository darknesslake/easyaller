using Easyaller.App;
using Easyaller.Core.Profiles;
using Easyaller.Deployment;

namespace Easyaller.Core.Tests;

public sealed class DeploymentPreparationControllerTests
{
    [Fact]
    public void CreatePreview_ExposesTheDocumentedTargetWithoutChangingTheProfile()
    {
        var profile = ProvisioningProfileFactory.CreateDefault();
        var target = new WindowsDeploymentTarget(WindowsEdition.Professional, WindowsArchitecture.Amd64, "24H2", 26100);

        var result = new DeploymentPreparationController().CreatePreview(profile, target);

        Assert.True(result.IsValid);
        Assert.Equal(DeploymentCompatibilityState.Documented, result.Preview!.CompatibilityState);
        Assert.Equal(profile.ProfileId, result.Preview.ProfileId);
        Assert.Equal(profile.Revision, result.Preview.ProfileRevision);
        Assert.True(result.Preview.IsFileOnly);
    }

    [Fact]
    public async Task CreateDryRun_ThenExportAsync_CreatesOnlyTheBasicFileOnlyPackage()
    {
        using var directory = new TemporaryDirectory();
        var profile = ProvisioningProfileFactory.CreateDefault();
        var target = new WindowsDeploymentTarget(WindowsEdition.Professional, WindowsArchitecture.Amd64, "24H2", 26100);
        var controller = new DeploymentPreparationController();

        var dryRun = controller.CreateDryRun(profile, target);
        var export = await controller.ExportAsync(dryRun.DryRun!, Path.Combine(directory.Path, "package"));

        Assert.True(dryRun.IsValid);
        Assert.True(export.IsSuccess);
        Assert.Equal(3, export.Manifest!.Files.Count);
        Assert.True(File.Exists(Path.Combine(export.DestinationDirectory!, "autounattend.xml")));
        Assert.True(File.Exists(Path.Combine(export.DestinationDirectory!, "selected-profile.wpprofile.json")));
        Assert.True(File.Exists(Path.Combine(export.DestinationDirectory!, "deployment-manifest.json")));
        Assert.False(Directory.Exists(Path.Combine(export.DestinationDirectory!, "payload")));
        Assert.False(Directory.Exists(Path.Combine(export.DestinationDirectory!, "scripts")));
        Assert.False(Directory.Exists(Path.Combine(export.DestinationDirectory!, "installers")));
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
