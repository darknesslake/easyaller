using Easyaller.Core.Profiles;
using Easyaller.Deployment;

namespace Easyaller.Core.Tests;

public sealed class PrivacyConfigurationServiceTests
{
    [Fact]
    public void CreatePlan_AllNotConfigured_IsACompleteNoOp()
    {
        var service = new PrivacyConfigurationService();
        var plan = service.CreatePlan(Settings(PrivacyPreference.NotConfigured), Target());
        var store = new RecordingPrivacyPolicyStore();
        var result = service.Apply(plan, store);

        Assert.True(plan.CanApply);
        Assert.Empty(plan.Assignments);
        Assert.Empty(plan.Warnings);
        Assert.True(result.IsApplied);
        Assert.Empty(store.Writes);
    }

    [Fact]
    public void Apply_DocumentedDisabledPolicies_WritesAndVerifiesExpectedDwordValues()
    {
        var service = new PrivacyConfigurationService();
        var settings = new PrivacySettings(
            PrivacyPreference.Disabled,
            PrivacyPreference.Disabled,
            PrivacyPreference.NotConfigured,
            PrivacyPreference.NotConfigured,
            PrivacyPreference.Disabled,
            PrivacyPreference.NotConfigured,
            PrivacyPreference.NotConfigured);
        var plan = service.CreatePlan(settings, Target());
        var store = new RecordingPrivacyPolicyStore();
        var result = service.Apply(plan, store);

        Assert.True(plan.CanApply);
        Assert.Empty(plan.Warnings);
        Assert.Equal(3, plan.Assignments.Count);
        Assert.True(result.IsApplied);
        Assert.All(result.Verification, verification => Assert.True(verification.IsVerified));
        Assert.Contains(store.Writes, write => write.ValueName == "LetAppsAccessLocation" && write.Value == 2);
        Assert.Contains(store.Writes, write => write.ValueName == "DisabledByGroupPolicy" && write.Value == 1);
        Assert.Contains(store.Writes, write => write.ValueName == "AllowInputPersonalization" && write.Value == 0);
    }

    [Fact]
    public void CreatePlan_UnsupportedTarget_BlocksAllPolicyWrites()
    {
        var service = new PrivacyConfigurationService();
        var plan = service.CreatePlan(Settings(PrivacyPreference.Disabled), Target(build: 26000));
        var store = new RecordingPrivacyPolicyStore();
        var result = service.Apply(plan, store);

        Assert.False(plan.CanApply);
        Assert.Contains(plan.Errors, error => error.Code == "privacy.target.unsupported");
        Assert.False(result.IsApplied);
        Assert.Empty(store.Writes);
    }

    [Fact]
    public void CreatePlan_UserChoice_DoesNotRemoveOrReplaceAnExistingPolicy()
    {
        var service = new PrivacyConfigurationService();
        var plan = service.CreatePlan(Settings(PrivacyPreference.UserChoice), Target());
        var store = new RecordingPrivacyPolicyStore();
        store.Set("Software\\Policies\\Microsoft\\Windows\\AppPrivacy", "LetAppsAccessLocation", 2);

        var result = service.Apply(plan, store);

        Assert.True(result.IsApplied);
        Assert.Empty(store.Writes);
        Assert.Equal(2, store.ReadDword("Software\\Policies\\Microsoft\\Windows\\AppPrivacy", "LetAppsAccessLocation"));
    }

    [Fact]
    public void CreatePlan_UnsupportedPreference_WarnsAndDoesNotInventARegistryMapping()
    {
        var service = new PrivacyConfigurationService();
        var settings = new PrivacySettings(
            PrivacyPreference.NotConfigured,
            PrivacyPreference.NotConfigured,
            PrivacyPreference.NotConfigured,
            PrivacyPreference.NotConfigured,
            PrivacyPreference.NotConfigured,
            PrivacyPreference.Disabled,
            PrivacyPreference.NotConfigured);
        var plan = service.CreatePlan(settings, Target());

        Assert.True(plan.CanApply);
        Assert.Empty(plan.Assignments);
        Assert.Contains(plan.Warnings, warning => warning.FieldPath == "windows.privacy.findMyDevice");
    }

    [Fact]
    public void Apply_ForgedRegistryAssignment_IsRejectedWithoutWriting()
    {
        var service = new PrivacyConfigurationService();
        var plan = new PrivacyConfigurationPlan(
            Target(),
            [new PrivacyPolicyAssignment(PrivacyPolicySetting.LocationServices, "Software\\Unsafe", "AnyValue", 1)],
            [],
            []);
        var store = new RecordingPrivacyPolicyStore();

        var result = service.Apply(plan, store);

        Assert.False(result.IsApplied);
        Assert.Contains(result.Errors, error => error.Code == "privacy.plan.assignment.invalid");
        Assert.Empty(store.Writes);
    }

    private static PrivacySettings Settings(PrivacyPreference preference) =>
        new(preference, preference, preference, preference, preference, preference, preference);

    private static WindowsDeploymentTarget Target(
        WindowsEdition edition = WindowsEdition.Professional,
        int build = 26100) =>
        new(edition, WindowsArchitecture.Amd64, "24H2", build);

    private sealed class RecordingPrivacyPolicyStore : IPrivacyPolicyStore
    {
        private readonly Dictionary<(string Key, string Name), int> _values = new();

        public List<(string RegistryKeyPath, string ValueName, int Value)> Writes { get; } = [];

        public int? ReadDword(string registryKeyPath, string valueName) =>
            _values.TryGetValue((registryKeyPath, valueName), out var value) ? value : null;

        public void WriteDword(string registryKeyPath, string valueName, int value)
        {
            Writes.Add((registryKeyPath, valueName, value));
            Set(registryKeyPath, valueName, value);
        }

        public void Set(string registryKeyPath, string valueName, int value) =>
            _values[(registryKeyPath, valueName)] = value;
    }
}
