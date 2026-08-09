using Easyaller.Core.Profiles;
using Easyaller.Core.Provisioning;
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
        Assert.DoesNotContain(result.Preview.ProvisioningSteps, step => step.Kind == ProvisioningStepKind.RequestDomainJoin);
        Assert.DoesNotContain(result.Preview.RuntimePrompts, prompt => prompt.Kind == RuntimePromptKind.DomainJoin);
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
        Assert.Contains(result.Warnings, warning => warning.Code == "deployment.target.version.unknown");
    }

    [Fact]
    public void Catalog_ContainsOnlyDocumentedInitialWindows11Targets()
    {
        var catalog = new Windows11CompatibilityCatalog();

        Assert.Equal(4, catalog.Entries.Count);
        Assert.All(catalog.Entries, entry =>
        {
            Assert.True(entry.Evidence.IsDocumented);
            Assert.False(entry.Evidence.IsSchemaValidated);
            Assert.False(entry.Evidence.IsVmValidated);
            Assert.Equal(WindowsArchitecture.Amd64, entry.Architecture);
            Assert.True(new[] { "24H2", "25H2" }.Contains(entry.DisplayVersion));
            Assert.Contains(DeploymentSetting.Oobe, entry.SupportedSettings);
            Assert.NotEmpty(entry.SourceLinks);
        });
    }

    [Fact]
    public void CreatePreview_KnownVersionWithUnexpectedBuild_WarnsInsteadOfGuessingSupport()
    {
        var profile = ProvisioningProfileFactory.CreateDefault();
        var request = new DeploymentPreparationRequest(
            profile,
            new WindowsDeploymentTarget(WindowsEdition.Professional, WindowsArchitecture.Amd64, "24H2", 26199));

        var result = new DeploymentPreviewService().CreatePreview(request);

        Assert.True(result.IsValid);
        Assert.Equal(DeploymentCompatibilityState.Warning, result.Preview!.CompatibilityState);
        Assert.Contains(result.Warnings, warning => warning.Code == "deployment.target.build.unknown");
    }

    [Fact]
    public void CreatePreview_DomainJoinProfile_IsBlockedBeforeAnyPackagePlan()
    {
        var profile = ProvisioningProfileFactory.CreateDefault() with
        {
            Domain = new DomainSettings(DomainMode.Required, CredentialHandling.PromptAtRuntime),
        };
        var request = new DeploymentPreparationRequest(
            profile,
            new WindowsDeploymentTarget(WindowsEdition.Professional, WindowsArchitecture.Amd64, "24H2", 26100));

        var result = new DeploymentPreviewService().CreatePreview(request);

        Assert.False(result.IsValid);
        Assert.Null(result.Preview);
        Assert.Contains(result.Errors, error => error.Code == "deployment.profile.domainJoin.required.forbidden");
    }

    [Fact]
    public void ValidateSerializedProfile_CredentialField_IsBlockedWithTheRequiredMessage()
    {
        var profileJson = new ProfileJsonSerializer().Serialize(ProvisioningProfileFactory.CreateDefault());
        var unsafeJson = System.Text.Encoding.UTF8.GetString(profileJson)
            .Replace("\"metadata\": {", "\"password\": \"not-allowed\",\n  \"metadata\": {", StringComparison.Ordinal);

        var result = new DeploymentProfileValidator().ValidateSerializedProfile(
            System.Text.Encoding.UTF8.GetBytes(unsafeJson),
            new WindowsDeploymentTarget(WindowsEdition.Professional, WindowsArchitecture.Amd64, "24H2", 26100));

        var error = Assert.Single(result.Errors);
        Assert.Equal("deployment.profile.credential.forbidden", error.Code);
        Assert.Equal("Deployment profile contains a forbidden credential field.", error.Message);
    }

    [Fact]
    public void CreatePreview_InvalidPrivacyPreference_IsBlocked()
    {
        var defaultProfile = ProvisioningProfileFactory.CreateDefault();
        var profile = defaultProfile with
        {
            Windows = defaultProfile.Windows with
            {
                Privacy = defaultProfile.Windows.Privacy with
                {
                    LocationServices = (PrivacyPreference)999,
                },
            },
        };
        var request = new DeploymentPreparationRequest(
            profile,
            new WindowsDeploymentTarget(WindowsEdition.Professional, WindowsArchitecture.Amd64, "24H2", 26100));

        var result = new DeploymentPreviewService().CreatePreview(request);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == "deployment.profile.privacy.invalid");
    }
}
