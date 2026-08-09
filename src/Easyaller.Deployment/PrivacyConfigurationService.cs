using Easyaller.Core.Profiles;
using Microsoft.Win32;
using System.Runtime.Versioning;

namespace Easyaller.Deployment;

public enum PrivacyPolicySetting
{
    LocationServices,
    AdvertisingId,
    OnlineSpeechRecognition,
}

public sealed record PrivacyPolicyAssignment(
    PrivacyPolicySetting Setting,
    string RegistryKeyPath,
    string ValueName,
    int ExpectedValue);

public sealed record PrivacyConfigurationMessage(string Code, string FieldPath, string Message);

public sealed record PrivacyConfigurationPlan(
    WindowsDeploymentTarget Target,
    IReadOnlyList<PrivacyPolicyAssignment> Assignments,
    IReadOnlyList<PrivacyConfigurationMessage> Errors,
    IReadOnlyList<PrivacyConfigurationMessage> Warnings)
{
    public bool CanApply => Errors.Count == 0;
}

public sealed record PrivacyPolicyVerification(
    PrivacyPolicyAssignment Assignment,
    bool IsVerified,
    int? ActualValue);

public sealed record PrivacyConfigurationApplyResult(
    bool IsApplied,
    IReadOnlyList<PrivacyConfigurationMessage> Errors,
    IReadOnlyList<PrivacyPolicyVerification> Verification);

public interface IPrivacyPolicyStore
{
    int? ReadDword(string registryKeyPath, string valueName);

    void WriteDword(string registryKeyPath, string valueName, int value);
}

[SupportedOSPlatform("windows")]
public sealed class WindowsRegistryPrivacyPolicyStore : IPrivacyPolicyStore
{
    public int? ReadDword(string registryKeyPath, string valueName)
    {
        EnsureWindows();
        using var key = Registry.LocalMachine.OpenSubKey(registryKeyPath, writable: false);
        return key?.GetValue(valueName) is int value ? value : null;
    }

    public void WriteDword(string registryKeyPath, string valueName, int value)
    {
        EnsureWindows();
        using var key = Registry.LocalMachine.CreateSubKey(registryKeyPath, writable: true)
            ?? throw new InvalidOperationException("Windows privacy policy registry key is unavailable.");
        key.SetValue(valueName, value, RegistryValueKind.DWord);
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows privacy policies can be applied only on Windows.");
        }
    }
}

public interface IPrivacyConfigurationService
{
    PrivacyConfigurationPlan CreatePlan(PrivacySettings settings, WindowsDeploymentTarget target);

    PrivacyConfigurationApplyResult Apply(PrivacyConfigurationPlan plan, IPrivacyPolicyStore policyStore);

    IReadOnlyList<PrivacyPolicyVerification> Verify(PrivacyConfigurationPlan plan, IPrivacyPolicyStore policyStore);
}

public sealed class PrivacyConfigurationService : IPrivacyConfigurationService
{
    private const string AppPrivacyKey = @"Software\Policies\Microsoft\Windows\AppPrivacy";
    private const string AdvertisingInfoKey = @"Software\Policies\Microsoft\Windows\AdvertisingInfo";
    private const string InputPersonalizationKey = @"Software\Policies\Microsoft\InputPersonalization";

    public PrivacyConfigurationPlan CreatePlan(PrivacySettings settings, WindowsDeploymentTarget target)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(target);

        var assignments = new List<PrivacyPolicyAssignment>();
        var errors = new List<PrivacyConfigurationMessage>();
        var warnings = new List<PrivacyConfigurationMessage>();
        if (!HasPolicyIntent(settings))
        {
            return new PrivacyConfigurationPlan(target, assignments, errors, warnings);
        }

        if (!IsSupportedTarget(target))
        {
            errors.Add(new PrivacyConfigurationMessage(
                "privacy.target.unsupported",
                "target",
                "Privacy policies are supported only for documented Windows 11 Pro or Enterprise amd64 targets at build 26100 or later."));
            return new PrivacyConfigurationPlan(target, assignments, errors, warnings);
        }

        AddLocationPolicy(settings.LocationServices, assignments);
        AddAdvertisingIdPolicy(settings.AdvertisingId, assignments, warnings);
        AddOnlineSpeechPolicy(settings.OnlineSpeechRecognition, assignments, warnings);
        AddUnsupportedPreferenceWarning("diagnosticData", settings.DiagnosticData, warnings);
        AddUnsupportedPreferenceWarning("tailoredExperiences", settings.TailoredExperiences, warnings);
        AddUnsupportedPreferenceWarning("findMyDevice", settings.FindMyDevice, warnings);
        AddUnsupportedPreferenceWarning("inkingAndTypingPersonalization", settings.InkingAndTypingPersonalization, warnings);

        return new PrivacyConfigurationPlan(target, assignments, errors, warnings);
    }

    public PrivacyConfigurationApplyResult Apply(PrivacyConfigurationPlan plan, IPrivacyPolicyStore policyStore)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(policyStore);
        var planErrors = ValidatePlanForApplication(plan);
        if (planErrors.Count > 0)
        {
            return new PrivacyConfigurationApplyResult(false, planErrors, []);
        }

        foreach (var assignment in plan.Assignments)
        {
            policyStore.WriteDword(assignment.RegistryKeyPath, assignment.ValueName, assignment.ExpectedValue);
        }

        var verification = Verify(plan, policyStore);
        var errors = verification
            .Where(static item => !item.IsVerified)
            .Select(static item => new PrivacyConfigurationMessage(
                "privacy.policy.verification.failed",
                item.Assignment.Setting.ToString(),
                "A Windows privacy policy did not match the expected value after application."))
            .ToArray();
        return new PrivacyConfigurationApplyResult(errors.Length == 0, errors, verification);
    }

    public IReadOnlyList<PrivacyPolicyVerification> Verify(PrivacyConfigurationPlan plan, IPrivacyPolicyStore policyStore)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(policyStore);
        if (ValidatePlanForApplication(plan).Count > 0)
        {
            return [];
        }

        return plan.Assignments
            .Select(assignment =>
            {
                var actualValue = policyStore.ReadDword(assignment.RegistryKeyPath, assignment.ValueName);
                return new PrivacyPolicyVerification(
                    assignment,
                    actualValue == assignment.ExpectedValue,
                    actualValue);
            })
            .ToArray();
    }

    private static void AddLocationPolicy(
        PrivacyPreference preference,
        ICollection<PrivacyPolicyAssignment> assignments)
    {
        if (preference is PrivacyPreference.NotConfigured or PrivacyPreference.UserChoice)
        {
            return;
        }

        assignments.Add(new PrivacyPolicyAssignment(
            PrivacyPolicySetting.LocationServices,
            AppPrivacyKey,
            "LetAppsAccessLocation",
            preference == PrivacyPreference.Enabled ? 1 : 2));
    }

    private static void AddAdvertisingIdPolicy(
        PrivacyPreference preference,
        ICollection<PrivacyPolicyAssignment> assignments,
        ICollection<PrivacyConfigurationMessage> warnings)
    {
        if (preference is PrivacyPreference.NotConfigured or PrivacyPreference.UserChoice)
        {
            return;
        }

        if (preference == PrivacyPreference.Disabled)
        {
            assignments.Add(new PrivacyPolicyAssignment(
                PrivacyPolicySetting.AdvertisingId,
                AdvertisingInfoKey,
                "DisabledByGroupPolicy",
                1));
            return;
        }

        warnings.Add(new PrivacyConfigurationMessage(
            "privacy.advertisingId.enabled.unsupported",
            "windows.privacy.advertisingId",
            "Windows documents a policy to turn off the advertising ID, not to force it on. No policy is written."));
    }

    private static void AddOnlineSpeechPolicy(
        PrivacyPreference preference,
        ICollection<PrivacyPolicyAssignment> assignments,
        ICollection<PrivacyConfigurationMessage> warnings)
    {
        if (preference is PrivacyPreference.NotConfigured or PrivacyPreference.UserChoice)
        {
            return;
        }

        if (preference == PrivacyPreference.Disabled)
        {
            assignments.Add(new PrivacyPolicyAssignment(
                PrivacyPolicySetting.OnlineSpeechRecognition,
                InputPersonalizationKey,
                "AllowInputPersonalization",
                0));
            return;
        }

        warnings.Add(new PrivacyConfigurationMessage(
            "privacy.onlineSpeechRecognition.enabled.unsupported",
            "windows.privacy.onlineSpeechRecognition",
            "Windows documents a policy to prevent online speech recognition, not to force it on. No policy is written."));
    }

    private static void AddUnsupportedPreferenceWarning(
        string fieldName,
        PrivacyPreference preference,
        ICollection<PrivacyConfigurationMessage> warnings)
    {
        if (preference is PrivacyPreference.NotConfigured or PrivacyPreference.UserChoice)
        {
            return;
        }

        warnings.Add(new PrivacyConfigurationMessage(
            "privacy.policy.mapping.unsupported",
            "windows.privacy." + fieldName,
            "No documented policy mapping is implemented for this privacy preference. No policy is written."));
    }

    private static bool HasPolicyIntent(PrivacySettings settings) =>
        new[]
        {
            settings.LocationServices,
            settings.AdvertisingId,
            settings.DiagnosticData,
            settings.TailoredExperiences,
            settings.OnlineSpeechRecognition,
            settings.FindMyDevice,
            settings.InkingAndTypingPersonalization,
        }.Any(static preference => preference is PrivacyPreference.Enabled or PrivacyPreference.Disabled);

    private static bool IsSupportedTarget(WindowsDeploymentTarget target) =>
        target.Architecture == WindowsArchitecture.Amd64 &&
        target.Edition is WindowsEdition.Professional or WindowsEdition.Enterprise &&
        target.Build >= 26100;

    private static IReadOnlyList<PrivacyConfigurationMessage> ValidatePlanForApplication(PrivacyConfigurationPlan plan)
    {
        if (!plan.CanApply)
        {
            return plan.Errors;
        }

        if (plan.Assignments.Count == 0)
        {
            return [];
        }

        if (!IsSupportedTarget(plan.Target))
        {
            return
            [
                new PrivacyConfigurationMessage(
                    "privacy.target.unsupported",
                    "target",
                    "Privacy policies are supported only for documented Windows 11 Pro or Enterprise amd64 targets at build 26100 or later."),
            ];
        }

        return plan.Assignments.All(IsKnownAssignment)
            ? []
            :
            [
                new PrivacyConfigurationMessage(
                    "privacy.plan.assignment.invalid",
                    "assignments",
                    "Privacy policy application accepts only fixed documented registry mappings."),
            ];
    }

    private static bool IsKnownAssignment(PrivacyPolicyAssignment assignment) =>
        assignment switch
        {
            {
                Setting: PrivacyPolicySetting.LocationServices,
                RegistryKeyPath: AppPrivacyKey,
                ValueName: "LetAppsAccessLocation",
                ExpectedValue: 1 or 2,
            } => true,
            {
                Setting: PrivacyPolicySetting.AdvertisingId,
                RegistryKeyPath: AdvertisingInfoKey,
                ValueName: "DisabledByGroupPolicy",
                ExpectedValue: 1,
            } => true,
            {
                Setting: PrivacyPolicySetting.OnlineSpeechRecognition,
                RegistryKeyPath: InputPersonalizationKey,
                ValueName: "AllowInputPersonalization",
                ExpectedValue: 0,
            } => true,
            _ => false,
        };
}
