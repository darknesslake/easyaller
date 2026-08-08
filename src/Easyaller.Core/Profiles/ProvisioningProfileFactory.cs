namespace Easyaller.Core.Profiles;

public static class ProvisioningProfileFactory
{
    public static ProvisioningProfile CreateDefault(string name = "Default workstation") => new()
    {
        Metadata = new ProfileMetadata(name, "Neutral Easyaller profile"),
        Windows = new WindowsSettings(
            [WindowsEdition.Professional, WindowsEdition.Enterprise],
            WindowsArchitecture.Amd64,
            new LocaleSettings("en-US", "en-US", "en-US", "en-US"),
            "UTC",
            new OobeSettings(null, null, null, false, null),
            new PrivacySettings(
                PrivacyPreference.NotConfigured,
                PrivacyPreference.NotConfigured,
                PrivacyPreference.NotConfigured,
                PrivacyPreference.NotConfigured,
                PrivacyPreference.NotConfigured,
                PrivacyPreference.NotConfigured,
                PrivacyPreference.NotConfigured)),
        Machine = new MachineSettings(
            new ComputerNameRule(ComputerNameMode.RequiredAtRuntime, null),
            new NetworkSettings(NetworkConfigurationMode.PromptAtRuntime),
            new ProxySettings(ProxyConfigurationMode.NotConfigured)),
        Domain = new DomainSettings(DomainMode.Optional, CredentialHandling.PromptAtRuntime),
        Deployment = new DeploymentSettings(ProvisionerLaunchMode.FirstLogon),
        Cleanup = new CleanupSettings(ProvisioningAccountCleanupMode.DisableAfterValidation),
    };
}
