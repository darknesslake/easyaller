using System.Text;
using Easyaller.App;
using Easyaller.Core.Profiles;
using Easyaller.Deployment;

namespace Easyaller.Core.Tests;

public sealed class FirstLogonBootstrapperTests
{
    [Fact]
    public async Task ExportAsync_EnabledBootstrap_WritesVerifiedFixedScriptAndApplicationPayload()
    {
        using var directory = new TemporaryDirectory();
        using var generated = new TemporaryLocalAccountCredentialFactory().Create();
        var applicationSource = Path.Combine(directory.Path, "Easyaller.App.exe");
        File.WriteAllText(applicationSource, "neutral test application");
        var request = new DeploymentPreparationRequest(
            ProvisioningProfileFactory.CreateDefault(),
            new WindowsDeploymentTarget(WindowsEdition.Professional, WindowsArchitecture.Amd64, "24H2", 26100),
            generated.Credential,
            EnableFirstLogonBootstrap: true);
        var dryRun = new DeploymentDryRunService().CreateDryRun(request).DryRun!;
        var packageDirectory = Path.Combine(directory.Path, "package");

        var export = await new DeploymentPackageExporter().ExportAsync(new DeploymentPackageExportRequest(
            dryRun,
            packageDirectory,
            [new DeploymentPackageAsset(
                DeploymentPackageAssetKind.LocalPayload,
                applicationSource,
                FirstLogonBootstrapper.RequiredApplicationPackageRelativePath)]));
        var payloadDirectory = Path.Combine(packageDirectory, "$OEM$", "$1", "ProgramData", "Easyaller");
        var scriptPath = Path.Combine(payloadDirectory, "scripts", "Start-EasyallerBootstrap.ps1");
        var script = File.ReadAllText(scriptPath, Encoding.UTF8);
        var passwordShownToAdministrator = generated.RevealPasswordOnce();

        Assert.True(export.IsSuccess);
        Assert.NotNull(dryRun.FirstLogonBootstrap);
        Assert.True(File.Exists(scriptPath));
        Assert.Contains("Get-FileHash", script, StringComparison.Ordinal);
        Assert.Contains(FirstLogonBootstrapper.RunOnceValueName, script, StringComparison.Ordinal);
        Assert.Contains("InitialBootstrap", script, StringComparison.Ordinal);
        Assert.Contains("ResumeBootstrap", script, StringComparison.Ordinal);
        Assert.Contains("Start-Process", script, StringComparison.Ordinal);
        Assert.Contains("selected-profile.wpprofile.json", script, StringComparison.Ordinal);
        Assert.Contains("Easyaller\\Profiles", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove-ItemProperty", script, StringComparison.Ordinal);
        Assert.NotNull(passwordShownToAdministrator);
        Assert.DoesNotContain(passwordShownToAdministrator, script, StringComparison.Ordinal);
        Assert.True(new ConfigurationSetPayloadVerifier().Verify(payloadDirectory).IsValid);
    }

    [Fact]
    public async Task ExportAsync_EnabledBootstrapWithoutApplicationPayload_IsBlocked()
    {
        using var directory = new TemporaryDirectory();
        using var generated = new TemporaryLocalAccountCredentialFactory().Create();
        var request = new DeploymentPreparationRequest(
            ProvisioningProfileFactory.CreateDefault(),
            new WindowsDeploymentTarget(WindowsEdition.Professional, WindowsArchitecture.Amd64, "24H2", 26100),
            generated.Credential,
            EnableFirstLogonBootstrap: true);
        var dryRun = new DeploymentDryRunService().CreateDryRun(request).DryRun!;

        var export = await new DeploymentPackageExporter().ExportAsync(new DeploymentPackageExportRequest(
            dryRun,
            Path.Combine(directory.Path, "package"),
            []));

        Assert.False(export.IsSuccess);
        Assert.Contains(export.Errors, error => error.Code == "deployment.firstLogon.application.missing");
    }

    [Fact]
    public void TryComplete_ExactResumeArgument_UsesTheCompletionStoreOnce()
    {
        var store = new RecordingCompletionStore();
        var service = new FirstLogonResumeCompletionService(store);

        var normalRun = service.TryComplete(["Easyaller.App.exe"]);
        var resumeRun = service.TryComplete(["Easyaller.App.exe", FirstLogonResumeCompletionService.ResumeArgument]);

        Assert.False(normalRun);
        Assert.True(resumeRun);
        Assert.Equal(1, store.CompletedCount);
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

    private sealed class RecordingCompletionStore : IFirstLogonResumeCompletionStore
    {
        public int CompletedCount { get; private set; }

        public void MarkCompleted() => CompletedCount++;
    }
}
