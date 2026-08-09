using Easyaller.Core.Profiles;

namespace Easyaller.Deployment;

public sealed class DeploymentDryRunService(
    IDeploymentPreviewService? deploymentPreviewService = null,
    IUnattendGenerator? unattendGenerator = null) : IDeploymentDryRunService
{
    private readonly IDeploymentPreviewService _deploymentPreviewService = deploymentPreviewService ?? new DeploymentPreviewService();
    private readonly IUnattendGenerator _unattendGenerator = unattendGenerator ?? new UnattendXmlGenerator();

    public DeploymentDryRunResult CreateDryRun(DeploymentPreparationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Profile);
        ArgumentNullException.ThrowIfNull(request.Target);

        var previewResult = _deploymentPreviewService.CreatePreview(request);
        if (!previewResult.IsValid)
        {
            return new DeploymentDryRunResult(null, previewResult.Errors, previewResult.Warnings);
        }

        try
        {
            var answerFile = _unattendGenerator.Generate(request);
            return new DeploymentDryRunResult(
                new DeploymentDryRun(
                    previewResult.Preview!,
                    request.Profile,
                    request.Profile.Windows.Oobe,
                    request.Profile.Windows.Privacy,
                    answerFile,
                    CreateSensitiveMaterialWarnings(request.TemporaryLocalAccount)),
                [],
                previewResult.Warnings);
        }
        catch (DeploymentGenerationException exception)
        {
            return new DeploymentDryRunResult(null, exception.Errors, previewResult.Warnings);
        }
    }

    private static IReadOnlyList<DeploymentSensitiveMaterialWarning> CreateSensitiveMaterialWarnings(
        EphemeralLocalAccountCredential? temporaryLocalAccount)
    {
        var warnings = new List<DeploymentSensitiveMaterialWarning>
        {
            new(
                "deployment.preview.profile.confidential",
                "Preview can contain organization-specific profile information. Do not commit or share it without review."),
        };

        if (temporaryLocalAccount is not null)
        {
            warnings.Add(new DeploymentSensitiveMaterialWarning(
                "deployment.preview.temporaryAccount.sensitive",
                "Preview answer file contains an obfuscated temporary local-account password. Treat it as sensitive material."));
        }

        return warnings;
    }
}
