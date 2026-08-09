using Easyaller.Core.Profiles;
using Easyaller.Deployment;

namespace Easyaller.Core.Tests;

public sealed class DeploymentPreviewServiceTests
{
    [Fact]
    public void CreatePreview_CompatibleTarget_ProducesAFileOnlyPlan()
    {
        var profile = ProvisioningProfileFactory.CreateDefault();
        var request = new DeploymentPreparationRequest(
            profile,
            new WindowsDeploymentTarget(WindowsEdition.Professional, WindowsArchitecture.Amd64, "24H2", 26100));

        var result = new DeploymentPreviewService().CreatePreview(request);
        var package = new DeploymentPackagePlanner().CreatePlan(result.Preview!);

        Assert.True(result.IsValid);
        Assert.True(result.Preview!.IsFileOnly);
        Assert.Contains(DeploymentOperationKind.GenerateAnswerFile, result.Preview.PlannedOperations);
        Assert.DoesNotContain(package.Files, file => file.RelativePath.Contains("disk", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(package.Files, file => file.RelativePath.Contains("usb", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CreatePreview_IncompatibleEdition_IsBlockedBeforeAnyPackagePlan()
    {
        var profile = ProvisioningProfileFactory.CreateDefault() with
        {
            Windows = ProvisioningProfileFactory.CreateDefault().Windows with
            {
                SupportedEditions = [WindowsEdition.Enterprise],
            },
        };
        var request = new DeploymentPreparationRequest(
            profile,
            new WindowsDeploymentTarget(WindowsEdition.Professional, WindowsArchitecture.Amd64, "24H2", 26100));

        var result = new DeploymentPreviewService().CreatePreview(request);

        Assert.False(result.IsValid);
        Assert.Null(result.Preview);
        Assert.Contains(result.Errors, error => error.Code == "deployment.target.edition.unsupported");
    }

    [Fact]
    public void CreatePreview_UnknownDisplayVersion_WarnsInsteadOfGuessingAWorkaround()
    {
        var profile = ProvisioningProfileFactory.CreateDefault();
        var request = new DeploymentPreparationRequest(
            profile,
            new WindowsDeploymentTarget(WindowsEdition.Professional, WindowsArchitecture.Amd64, string.Empty, 26100));

        var result = new DeploymentPreviewService().CreatePreview(request);

        Assert.True(result.IsValid);
        Assert.Equal(DeploymentCompatibilityState.Warning, result.Preview!.CompatibilityState);
        Assert.Contains(result.Warnings, warning => warning.Code == "deployment.target.displayVersion.unknown");
    }
}
