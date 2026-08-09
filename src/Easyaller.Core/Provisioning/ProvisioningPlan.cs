using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Easyaller.Core.Profiles;

namespace Easyaller.Core.Provisioning;

public enum ProvisioningStepKind
{
    RequestComputerName,
    RequestNetworkConfiguration,
    RequestProxyConfiguration,
    RequestDomainJoin,
    ApplyPrivacyPreferences,
    InstallApplication,
    ShowInstruction,
    LaunchAtFirstLogon,
    ConfigureTemporaryAccountCleanup,
}

public enum RuntimePromptKind
{
    ComputerName,
    NetworkConfiguration,
    ProxyConfiguration,
    DomainJoin,
}

public sealed record ProvisioningStep(
    ProvisioningStepKind Kind,
    string Id,
    string Description);

public sealed record RuntimePrompt(
    RuntimePromptKind Kind,
    bool IsRequired,
    string Description);

public sealed record ProvisioningPlan(
    Guid ProfileId,
    int ProfileRevision,
    IReadOnlyList<ProvisioningStep> Steps,
    IReadOnlyList<RuntimePrompt> RuntimePrompts);

public sealed record ProvisioningPlanResult(
    ProvisioningPlan? Plan,
    IReadOnlyList<ProfileValidationError> Errors)
{
    public bool IsValid => Plan is not null && Errors.Count == 0;
}

public interface IProvisioningPlanBuilder
{
    ProvisioningPlanResult Create(ProvisioningProfile profile);
}

public sealed class ProvisioningPlanBuilder(ProvisioningProfileValidator? validator = null) : IProvisioningPlanBuilder
{
    private readonly ProvisioningProfileValidator _validator = validator ?? new ProvisioningProfileValidator();

    public ProvisioningPlanResult Create(ProvisioningProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var validation = _validator.Validate(profile);
        if (!validation.IsValid)
        {
            return new ProvisioningPlanResult(null, validation.Errors);
        }

        var steps = new List<ProvisioningStep>();
        var prompts = new List<RuntimePrompt>();

        AddPrompt(
            steps,
            prompts,
            RuntimePromptKind.ComputerName,
            isRequired: true,
            "computer-name",
            "Choose the final computer name at runtime.");

        if (profile.Machine.Network.Mode == NetworkConfigurationMode.PromptAtRuntime)
        {
            AddPrompt(
                steps,
                prompts,
                RuntimePromptKind.NetworkConfiguration,
                isRequired: true,
                "network",
                "Choose the network adapter and network configuration at runtime.");
        }

        if (profile.Machine.Proxy.Mode == ProxyConfigurationMode.PromptAtRuntime)
        {
            AddPrompt(
                steps,
                prompts,
                RuntimePromptKind.ProxyConfiguration,
                isRequired: true,
                "proxy",
                "Enter proxy settings at runtime.");
        }

        if (profile.Domain.Mode != DomainMode.NotConfigured)
        {
            AddPrompt(
                steps,
                prompts,
                RuntimePromptKind.DomainJoin,
                profile.Domain.Mode == DomainMode.Required,
                "domain-join",
                "Enter domain join settings and short-lived credentials at runtime.");
        }

        if (HasConfiguredPrivacyPreference(profile.Windows.Privacy))
        {
            steps.Add(new ProvisioningStep(
                ProvisioningStepKind.ApplyPrivacyPreferences,
                "privacy",
                "Apply supported, explicitly configured privacy preferences."));
        }

        foreach (var application in profile.Applications)
        {
            steps.Add(new ProvisioningStep(
                ProvisioningStepKind.InstallApplication,
                application.Id,
                $"Apply application profile: {application.DisplayName}."));
        }

        foreach (var instruction in profile.Instructions)
        {
            steps.Add(new ProvisioningStep(
                ProvisioningStepKind.ShowInstruction,
                instruction.Id,
                $"Show instruction: {instruction.Title}."));
        }

        if (profile.Deployment.LaunchMode == ProvisionerLaunchMode.FirstLogon)
        {
            steps.Add(new ProvisioningStep(
                ProvisioningStepKind.LaunchAtFirstLogon,
                "first-logon",
                "Launch Easyaller after a manual local provisioning sign-in."));
        }

        steps.Add(new ProvisioningStep(
            ProvisioningStepKind.ConfigureTemporaryAccountCleanup,
            "temporary-account-cleanup",
            "Keep temporary-account cleanup gated on post-provisioning validation."));

        return new ProvisioningPlanResult(
            new ProvisioningPlan(profile.ProfileId, profile.Revision, steps, prompts),
            []);
    }

    private static void AddPrompt(
        ICollection<ProvisioningStep> steps,
        ICollection<RuntimePrompt> prompts,
        RuntimePromptKind promptKind,
        bool isRequired,
        string id,
        string description)
    {
        prompts.Add(new RuntimePrompt(promptKind, isRequired, description));
        steps.Add(new ProvisioningStep(
            promptKind switch
            {
                RuntimePromptKind.ComputerName => ProvisioningStepKind.RequestComputerName,
                RuntimePromptKind.NetworkConfiguration => ProvisioningStepKind.RequestNetworkConfiguration,
                RuntimePromptKind.ProxyConfiguration => ProvisioningStepKind.RequestProxyConfiguration,
                RuntimePromptKind.DomainJoin => ProvisioningStepKind.RequestDomainJoin,
                _ => throw new ArgumentOutOfRangeException(nameof(promptKind)),
            },
            id,
            description));
    }

    private static bool HasConfiguredPrivacyPreference(PrivacySettings privacy) =>
        new[]
        {
            privacy.LocationServices,
            privacy.AdvertisingId,
            privacy.DiagnosticData,
            privacy.TailoredExperiences,
            privacy.OnlineSpeechRecognition,
            privacy.FindMyDevice,
            privacy.InkingAndTypingPersonalization,
        }.Any(preference => preference != PrivacyPreference.NotConfigured);
}

public sealed class RuntimeProvisioningInputs : IDisposable
{
    private RuntimeDomainCredential? _domainCredential;

    public string? ComputerName { get; init; }

    public string? NetworkAdapterId { get; init; }

    public string? ProxyAddress { get; init; }

    public string? DomainName { get; init; }

    public RuntimeDomainCredential? DomainCredential
    {
        get => _domainCredential;
        init => _domainCredential = value;
    }

    public void Dispose()
    {
        _domainCredential?.Dispose();
        _domainCredential = null;
        GC.SuppressFinalize(this);
    }

    public override string ToString() => "Runtime provisioning inputs are redacted.";
}

public sealed record RuntimeInputValidationResult(IReadOnlyList<RuntimeInputValidationError> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

public sealed record RuntimeInputValidationError(string Code, string FieldPath, string Message);

public sealed class RuntimeProvisioningInputValidator
{
    private static readonly Regex ComputerNamePattern = new("^[A-Za-z0-9-]{1,15}$", RegexOptions.CultureInvariant);

    public RuntimeInputValidationResult Validate(ProvisioningPlan plan, RuntimeProvisioningInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(inputs);

        var errors = new List<RuntimeInputValidationError>();
        foreach (var prompt in plan.RuntimePrompts)
        {
            switch (prompt.Kind)
            {
                case RuntimePromptKind.ComputerName:
                    if (string.IsNullOrWhiteSpace(inputs.ComputerName))
                    {
                        errors.Add(new RuntimeInputValidationError(
                            "runtime.computerName.required",
                            "computerName",
                            "Computer name is required."));
                    }
                    else if (!ComputerNamePattern.IsMatch(inputs.ComputerName))
                    {
                        errors.Add(new RuntimeInputValidationError(
                            "runtime.computerName.invalid",
                            "computerName",
                            "Computer name must use 1 to 15 letters, digits, or hyphens."));
                    }

                    break;

                case RuntimePromptKind.NetworkConfiguration when prompt.IsRequired && string.IsNullOrWhiteSpace(inputs.NetworkAdapterId):
                    errors.Add(new RuntimeInputValidationError(
                        "runtime.network.adapter.required",
                        "networkAdapterId",
                        "Network adapter selection is required."));
                    break;

                case RuntimePromptKind.ProxyConfiguration when prompt.IsRequired && string.IsNullOrWhiteSpace(inputs.ProxyAddress):
                    errors.Add(new RuntimeInputValidationError(
                        "runtime.proxy.required",
                        "proxyAddress",
                        "Proxy address is required."));
                    break;

                case RuntimePromptKind.DomainJoin:
                    ValidateDomainJoin(prompt, inputs, errors);
                    break;
            }
        }

        return new RuntimeInputValidationResult(errors);
    }

    private static void ValidateDomainJoin(
        RuntimePrompt prompt,
        RuntimeProvisioningInputs inputs,
        ICollection<RuntimeInputValidationError> errors)
    {
        if (prompt.IsRequired && string.IsNullOrWhiteSpace(inputs.DomainName))
        {
            errors.Add(new RuntimeInputValidationError(
                "runtime.domain.required",
                "domainName",
                "Domain name is required."));
        }

        var joinRequested = !string.IsNullOrWhiteSpace(inputs.DomainName) || inputs.DomainCredential is not null;
        if (joinRequested && inputs.DomainCredential is null)
        {
            errors.Add(new RuntimeInputValidationError(
                "runtime.domain.credential.required",
                "domainCredential",
                "Domain join credentials are required when domain join is requested."));
        }
    }
}

public sealed class RuntimeDomainCredential : IDisposable
{
    private char[]? _password;

    public RuntimeDomainCredential(string userName, ReadOnlySpan<char> password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        if (password.IsEmpty)
        {
            throw new ArgumentException("Password is required.", nameof(password));
        }

        UserName = userName;
        _password = password.ToArray();
    }

    public string UserName { get; }

    public bool IsDisposed => _password is null;

    public ReadOnlySpan<char> GetPasswordForExecution()
    {
        ObjectDisposedException.ThrowIf(_password is null, this);
        return _password;
    }

    public void Dispose()
    {
        if (_password is not null)
        {
            CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(_password.AsSpan()));
            _password = null;
        }

        GC.SuppressFinalize(this);
    }

    public override string ToString() => "Runtime domain credential is redacted.";
}
