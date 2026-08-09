using System.Text;
using Easyaller.Core.Profiles;
using Easyaller.Deployment;

namespace Easyaller.Core.Tests;

public sealed class DeploymentDryRunServiceTests
{
    [Fact]
    public void CreateDryRun_ReturnsTheSameInMemoryXmlAsTheDeploymentGenerator()
    {
        var defaultProfile = ProvisioningProfileFactory.CreateDefault();
        var profile = defaultProfile with
        {
            Windows = defaultProfile.Windows with
            {
                Oobe = defaultProfile.Windows.Oobe with
                {
                    HideEula = true,
                    ProtectYourPc = 1,
                },
                Privacy = defaultProfile.Windows.Privacy with
                {
                    LocationServices = PrivacyPreference.Disabled,
                },
            },
        };
        var request = CreateRequest(profile);
        var expectedAnswerFile = new UnattendXmlGenerator().Generate(request);

        var result = new DeploymentDryRunService().CreateDryRun(request);

        Assert.True(result.IsValid);
        Assert.NotNull(result.DryRun);
        Assert.True(result.DryRun.IsFileOnly);
        Assert.Equal(expectedAnswerFile, result.DryRun.AnswerFile.ToArray());
        Assert.Same(profile, result.DryRun.EffectiveProfile);
        Assert.Equal(profile.Windows.Oobe, result.DryRun.Oobe);
        Assert.Equal(profile.Windows.Privacy, result.DryRun.Privacy);
        Assert.Equal(DeploymentCompatibilityState.Documented, result.DryRun.Preview.CompatibilityState);
        Assert.Contains(result.DryRun.SensitiveMaterialWarnings, warning =>
            warning.Code == "deployment.preview.profile.confidential");
    }

    [Fact]
    public void CreateDryRun_TemporaryLocalAccount_ReturnsAWarningWithoutThePassword()
    {
        using var generated = new TemporaryLocalAccountCredentialFactory().Create();
        var passwordShownToAdministrator = generated.RevealPasswordOnce();
        var request = CreateRequest(ProvisioningProfileFactory.CreateDefault(), generated.Credential);

        var result = new DeploymentDryRunService().CreateDryRun(request);

        var warning = Assert.Single(
            result.DryRun!.SensitiveMaterialWarnings,
            static warning => warning.Code == "deployment.preview.temporaryAccount.sensitive");
        Assert.NotNull(passwordShownToAdministrator);
        Assert.DoesNotContain(passwordShownToAdministrator, warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            passwordShownToAdministrator,
            Encoding.UTF8.GetString(result.DryRun.AnswerFile.Span),
            StringComparison.Ordinal);
    }

    [Fact]
    public void CreateDryRun_RequiredDomainJoin_ReturnsValidationErrorAndNoXml()
    {
        var profile = ProvisioningProfileFactory.CreateDefault() with
        {
            Domain = new DomainSettings(DomainMode.Required, CredentialHandling.PromptAtRuntime),
        };

        var result = new DeploymentDryRunService().CreateDryRun(CreateRequest(profile));

        Assert.False(result.IsValid);
        Assert.Null(result.DryRun);
        Assert.Contains(result.Errors, error => error.Code == "deployment.profile.domainJoin.required.forbidden");
    }

    [Fact]
    public void CreateDryRun_GeneratorValidationFailure_ReturnsItsErrorsWithoutWritingAFile()
    {
        var result = new DeploymentDryRunService(unattendGenerator: new FailingUnattendGenerator())
            .CreateDryRun(CreateRequest(ProvisioningProfileFactory.CreateDefault()));

        Assert.False(result.IsValid);
        Assert.Null(result.DryRun);
        Assert.Contains(result.Errors, error => error.Code == "deployment.dryRun.generation.blocked");
    }

    private static DeploymentPreparationRequest CreateRequest(
        ProvisioningProfile profile,
        EphemeralLocalAccountCredential? temporaryLocalAccount = null) => new(
            profile,
            new WindowsDeploymentTarget(WindowsEdition.Professional, WindowsArchitecture.Amd64, "24H2", 26100),
            temporaryLocalAccount);

    private sealed class FailingUnattendGenerator : IUnattendGenerator
    {
        public byte[] Generate(DeploymentPreparationRequest request) => throw new DeploymentGenerationException(
            [new DeploymentValidationError(
                "deployment.dryRun.generation.blocked",
                "answerFile",
                "Answer-file generation was deliberately blocked for this test.")]);
    }
}
