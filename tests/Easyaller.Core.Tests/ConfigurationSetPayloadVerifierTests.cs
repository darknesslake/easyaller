using System.Text;
using Easyaller.Core.Profiles;
using Easyaller.Deployment;

namespace Easyaller.Core.Tests;

public sealed class ConfigurationSetPayloadVerifierTests
{
    [Fact]
    public async Task Verify_ExportedOemPayload_ValidatesEveryDeliveredFile()
    {
        using var directory = new TemporaryDirectory();
        var sourcePath = Path.Combine(directory.Path, "settings.json");
        File.WriteAllText(sourcePath, "{\"setting\":true}\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var profile = ProvisioningProfileFactory.CreateDefault();
        var dryRun = new DeploymentDryRunService().CreateDryRun(new DeploymentPreparationRequest(
            profile,
            new WindowsDeploymentTarget(WindowsEdition.Professional, WindowsArchitecture.Amd64, "24H2", 26100))).DryRun!;
        var packageDirectory = Path.Combine(directory.Path, "package");

        var export = await new DeploymentPackageExporter().ExportAsync(new DeploymentPackageExportRequest(
            dryRun,
            packageDirectory,
            [new DeploymentPackageAsset(
                DeploymentPackageAssetKind.LocalPayload,
                sourcePath,
                ConfigurationSetPayloadLayout.RootRelativePath + "/payload/settings.json")]));
        var payloadDirectory = Path.Combine(packageDirectory, "$OEM$", "$1", "ProgramData", "Easyaller");

        var verification = new ConfigurationSetPayloadVerifier().Verify(payloadDirectory);

        Assert.True(export.IsSuccess);
        Assert.True(verification.IsValid);
        Assert.Equal(1, verification.VerifiedFileCount);
    }

    [Fact]
    public async Task Verify_ChangedDeliveredFile_ReportsHashMismatchWithoutWritingAnything()
    {
        using var directory = new TemporaryDirectory();
        var sourcePath = Path.Combine(directory.Path, "settings.json");
        File.WriteAllText(sourcePath, "{\"setting\":true}\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var profile = ProvisioningProfileFactory.CreateDefault();
        var dryRun = new DeploymentDryRunService().CreateDryRun(new DeploymentPreparationRequest(
            profile,
            new WindowsDeploymentTarget(WindowsEdition.Professional, WindowsArchitecture.Amd64, "24H2", 26100))).DryRun!;
        var packageDirectory = Path.Combine(directory.Path, "package");
        await new DeploymentPackageExporter().ExportAsync(new DeploymentPackageExportRequest(
            dryRun,
            packageDirectory,
            [new DeploymentPackageAsset(
                DeploymentPackageAssetKind.LocalPayload,
                sourcePath,
                ConfigurationSetPayloadLayout.RootRelativePath + "/payload/settings.json")]));
        var payloadDirectory = Path.Combine(packageDirectory, "$OEM$", "$1", "ProgramData", "Easyaller");
        var payloadFilePath = Path.Combine(payloadDirectory, "payload", "settings.json");
        File.WriteAllText(payloadFilePath, "changed");

        var verification = new ConfigurationSetPayloadVerifier().Verify(payloadDirectory);

        Assert.False(verification.IsValid);
        Assert.Equal(0, verification.VerifiedFileCount);
        Assert.Contains(verification.Errors, error => error.Code == "deployment.payload.hash.mismatch");
        Assert.Equal("changed", File.ReadAllText(payloadFilePath));
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
