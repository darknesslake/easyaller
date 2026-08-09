using Easyaller.Core.Profiles;
using Easyaller.Core.Provisioning;

namespace Easyaller.Core.Tests;

public sealed class ProvisioningPlanBuilderTests
{
    [Fact]
    public void Create_DefaultProfile_MapsRuntimePromptsWithoutExecutingAnything()
    {
        var profile = ProvisioningProfileFactory.CreateDefault() with
        {
            Applications =
            [
                new ApplicationProfile("example-app", "Example app", ApplicationSourceKind.ExternalManual, null, []),
            ],
            Instructions = [new InstructionProfile("after-setup", "After setup", "Review the handoff.")],
        };

        var result = new ProvisioningPlanBuilder().Create(profile);

        Assert.True(result.IsValid);
        Assert.Equal(profile.ProfileId, result.Plan!.ProfileId);
        Assert.Contains(result.Plan.RuntimePrompts, prompt => prompt.Kind == RuntimePromptKind.ComputerName && prompt.IsRequired);
        Assert.Contains(result.Plan.RuntimePrompts, prompt => prompt.Kind == RuntimePromptKind.NetworkConfiguration && prompt.IsRequired);
        Assert.Contains(result.Plan.RuntimePrompts, prompt => prompt.Kind == RuntimePromptKind.DomainJoin && !prompt.IsRequired);
        Assert.Contains(result.Plan.Steps, step => step.Kind == ProvisioningStepKind.InstallApplication && step.Id == "example-app");
        Assert.Contains(result.Plan.Steps, step => step.Kind == ProvisioningStepKind.ShowInstruction && step.Id == "after-setup");
    }

    [Fact]
    public void Create_NotConfiguredValues_DoNotProduceSystemChangeSteps()
    {
        var defaultProfile = ProvisioningProfileFactory.CreateDefault();
        var profile = defaultProfile with
        {
            Domain = defaultProfile.Domain with { Mode = DomainMode.NotConfigured },
            Windows = defaultProfile.Windows with
            {
                Privacy = new PrivacySettings(
                    PrivacyPreference.NotConfigured,
                    PrivacyPreference.NotConfigured,
                    PrivacyPreference.NotConfigured,
                    PrivacyPreference.NotConfigured,
                    PrivacyPreference.NotConfigured,
                    PrivacyPreference.NotConfigured,
                    PrivacyPreference.NotConfigured),
            },
            Deployment = new DeploymentSettings(ProvisionerLaunchMode.Manual),
        };

        var result = new ProvisioningPlanBuilder().Create(profile);

        Assert.True(result.IsValid);
        Assert.DoesNotContain(result.Plan!.RuntimePrompts, prompt => prompt.Kind == RuntimePromptKind.DomainJoin);
        Assert.DoesNotContain(result.Plan.Steps, step => step.Kind == ProvisioningStepKind.ApplyPrivacyPreferences);
        Assert.DoesNotContain(result.Plan.Steps, step => step.Kind == ProvisioningStepKind.LaunchAtFirstLogon);
    }

    [Fact]
    public void RuntimeDomainCredential_Dispose_ZeroizesTheEphemeralPasswordAndRedactsOutput()
    {
        var credential = new RuntimeDomainCredential("CONTOSO\\Administrator", "temporary-password".AsSpan());
        using var inputs = new RuntimeProvisioningInputs { DomainCredential = credential };

        Assert.Equal("Runtime domain credential is redacted.", credential.ToString());
        Assert.Equal("Runtime provisioning inputs are redacted.", inputs.ToString());
        credential.Dispose();

        Assert.True(credential.IsDisposed);
    }

    [Fact]
    public void Validate_RequiredRuntimeInputs_ReportsOnlyMissingOrUnsafeValues()
    {
        var profile = ProvisioningProfileFactory.CreateDefault();
        var plan = new ProvisioningPlanBuilder().Create(profile).Plan!;
        using var inputs = new RuntimeProvisioningInputs
        {
            ComputerName = "invalid computer name",
            NetworkAdapterId = null,
        };

        var result = new RuntimeProvisioningInputValidator().Validate(plan, inputs);

        Assert.Contains(result.Errors, error => error.Code == "runtime.computerName.invalid");
        Assert.Contains(result.Errors, error => error.Code == "runtime.network.adapter.required");
        Assert.DoesNotContain(result.Errors, error => error.Code == "runtime.domain.required");
    }

    [Fact]
    public void Create_StaticIpv4Profile_KeepsAddressValuesInPlanAndRequiresAdapterChoice()
    {
        var defaultProfile = ProvisioningProfileFactory.CreateDefault();
        var profile = defaultProfile with
        {
            Machine = defaultProfile.Machine with
            {
                Network = new NetworkSettings(
                    NetworkConfigurationMode.StaticIpv4,
                    new StaticIpv4Configuration("192.0.2.77", "255.255.255.0", "192.0.2.254", ["192.0.2.53"])),
            },
        };

        var result = new ProvisioningPlanBuilder().Create(profile);

        Assert.True(result.IsValid);
        Assert.Equal("192.0.2.77", result.Plan!.StaticIpv4!.Address);
        Assert.Contains(result.Plan.RuntimePrompts, prompt => prompt.Kind == RuntimePromptKind.NetworkConfiguration && prompt.IsRequired);
        Assert.Contains(result.Plan.Steps, step => step.Kind == ProvisioningStepKind.ConfigureStaticIpv4);
    }
}
