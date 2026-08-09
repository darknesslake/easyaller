using Easyaller.Core.Profiles;
using Easyaller.Deployment;

namespace Easyaller.App;

public sealed class DeploymentPreparationController(
    IDeploymentPreviewService? previewService = null,
    IDeploymentDryRunService? dryRunService = null,
    IDeploymentPackageExporter? packageExporter = null)
{
    private readonly IDeploymentPreviewService _previewService = previewService ?? new DeploymentPreviewService();
    private readonly IDeploymentDryRunService _dryRunService = dryRunService ?? new DeploymentDryRunService();
    private readonly IDeploymentPackageExporter _packageExporter = packageExporter ?? new DeploymentPackageExporter();

    public DeploymentPreviewResult CreatePreview(ProvisioningProfile profile, WindowsDeploymentTarget target) =>
        _previewService.CreatePreview(new DeploymentPreparationRequest(profile, target));

    public DeploymentDryRunResult CreateDryRun(ProvisioningProfile profile, WindowsDeploymentTarget target) =>
        _dryRunService.CreateDryRun(new DeploymentPreparationRequest(profile, target));

    public Task<DeploymentPackageExportResult> ExportAsync(
        DeploymentDryRun dryRun,
        string destinationDirectory,
        CancellationToken cancellationToken = default) =>
        _packageExporter.ExportAsync(
            new DeploymentPackageExportRequest(dryRun, destinationDirectory, []),
            cancellationToken);
}
