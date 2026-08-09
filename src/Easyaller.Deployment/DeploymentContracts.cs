using Easyaller.Core.Profiles;
using Easyaller.Core.Provisioning;

namespace Easyaller.Deployment;

public enum DeploymentCompatibilityState
{
    Documented,
    SchemaValidated,
    VmValidated,
    Warning,
    Unsupported,
}

public enum DeploymentOperationKind
{
    GenerateAnswerFile,
    CreateManifest,
    CopyLocalPayload,
    CopyAllowedInstaller,
}

public enum DeploymentSetting
{
    Locale,
    TimeZone,
    Oobe,
    LocalAccount,
    FirstLogon,
}

public sealed record WindowsBuildRange(int MinimumBuild, int MaximumBuild)
{
    public bool Contains(int build) => build >= MinimumBuild && build <= MaximumBuild;
}

public sealed record CompatibilityEvidence(
    bool IsDocumented,
    bool IsSchemaValidated,
    bool IsVmValidated);

public sealed record WindowsCompatibilityEntry(
    WindowsEdition Edition,
    WindowsArchitecture Architecture,
    string DisplayVersion,
    WindowsBuildRange BuildRange,
    IReadOnlySet<DeploymentSetting> SupportedSettings,
    IReadOnlyList<Uri> SourceLinks,
    CompatibilityEvidence Evidence);

public sealed record WindowsDeploymentTarget(
    WindowsEdition Edition,
    WindowsArchitecture Architecture,
    string DisplayVersion,
    int Build);

public sealed record DeploymentCompatibilityResult(
    DeploymentCompatibilityState State,
    IReadOnlyList<DeploymentValidationError> Errors,
    IReadOnlyList<DeploymentValidationError> Warnings)
{
    public bool CanContinue => State is not DeploymentCompatibilityState.Unsupported && Errors.Count == 0;
}

public sealed record DeploymentValidationError(string Code, string FieldPath, string Message);

public sealed record DeploymentPreparationRequest(
    ProvisioningProfile Profile,
    WindowsDeploymentTarget Target,
    EphemeralLocalAccountCredential? TemporaryLocalAccount = null);

public sealed record DeploymentPreview(
    Guid ProfileId,
    int ProfileRevision,
    WindowsDeploymentTarget Target,
    DeploymentCompatibilityState CompatibilityState,
    IReadOnlyList<ProvisioningStep> ProvisioningSteps,
    IReadOnlyList<RuntimePrompt> RuntimePrompts,
    IReadOnlyList<DeploymentOperationKind> PlannedOperations,
    bool IsFileOnly);

public sealed record DeploymentPreviewResult(
    DeploymentPreview? Preview,
    IReadOnlyList<DeploymentValidationError> Errors,
    IReadOnlyList<DeploymentValidationError> Warnings)
{
    public bool IsValid => Preview is not null && Errors.Count == 0;
}

public sealed record DeploymentPackagePlan(
    DeploymentPreview Preview,
    IReadOnlyList<DeploymentPackageFile> Files);

public sealed record DeploymentPackageFile(string RelativePath, string Purpose);

public interface IDeploymentCompatibilityValidator
{
    DeploymentCompatibilityResult Validate(WindowsDeploymentTarget target, ProvisioningProfile profile);
}

public interface IDeploymentProfileValidator
{
    DeploymentProfileValidationResult Validate(DeploymentPreparationRequest request);
}

public sealed record DeploymentProfileValidationResult(
    IReadOnlyList<DeploymentValidationError> Errors,
    IReadOnlyList<DeploymentValidationError> Warnings,
    DeploymentCompatibilityState CompatibilityState)
{
    public bool IsValid => Errors.Count == 0;
}

public interface IWindowsCompatibilityCatalog
{
    IReadOnlyList<WindowsCompatibilityEntry> Entries { get; }

    WindowsCompatibilityEntry? Find(WindowsDeploymentTarget target);
}

public interface IDeploymentPreviewService
{
    DeploymentPreviewResult CreatePreview(DeploymentPreparationRequest request);
}

public interface IUnattendGenerator
{
    byte[] Generate(DeploymentPreparationRequest request);
}

public interface IDeploymentPackagePlanner
{
    DeploymentPackagePlan CreatePlan(DeploymentPreview preview);
}

public interface IDeploymentPackageExporter
{
    Task ExportAsync(DeploymentPackagePlan plan, string destinationDirectory, CancellationToken cancellationToken = default);
}

public sealed class BasicDeploymentCompatibilityValidator : IDeploymentCompatibilityValidator
{
    public DeploymentCompatibilityResult Validate(WindowsDeploymentTarget target, ProvisioningProfile profile)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(profile);

        var errors = new List<DeploymentValidationError>();
        var warnings = new List<DeploymentValidationError>();
        if (target.Build < 1)
        {
            errors.Add(new DeploymentValidationError(
                "deployment.target.build.invalid",
                "target.build",
                "Windows build must be positive."));
        }

        if (target.Architecture != profile.Windows.Architecture)
        {
            errors.Add(new DeploymentValidationError(
                "deployment.target.architecture.unsupported",
                "target.architecture",
                "Target architecture does not match the profile."));
        }

        if (!profile.Windows.SupportedEditions.Contains(target.Edition))
        {
            errors.Add(new DeploymentValidationError(
                "deployment.target.edition.unsupported",
                "target.edition",
                "Target edition is not supported by the profile."));
        }

        return new DeploymentCompatibilityResult(
            errors.Count > 0
                ? DeploymentCompatibilityState.Unsupported
                : warnings.Count > 0
                    ? DeploymentCompatibilityState.Warning
                    : DeploymentCompatibilityState.Documented,
            errors,
            warnings);
    }
}

public sealed class Windows11CompatibilityCatalog : IWindowsCompatibilityCatalog
{
    private const string Windows11ReleaseInformationUrl = "https://learn.microsoft.com/en-us/windows/release-health/windows11-release-information";
    private static readonly IReadOnlySet<DeploymentSetting> InitialSupportedSettings = new HashSet<DeploymentSetting>
    {
        DeploymentSetting.Locale,
        DeploymentSetting.TimeZone,
        DeploymentSetting.Oobe,
        DeploymentSetting.LocalAccount,
        DeploymentSetting.FirstLogon,
    };

    public IReadOnlyList<WindowsCompatibilityEntry> Entries { get; } =
    [
        CreateEntry(WindowsEdition.Professional, "24H2", 26100),
        CreateEntry(WindowsEdition.Enterprise, "24H2", 26100),
        CreateEntry(WindowsEdition.Professional, "25H2", 26200),
        CreateEntry(WindowsEdition.Enterprise, "25H2", 26200),
    ];

    public WindowsCompatibilityEntry? Find(WindowsDeploymentTarget target) => Entries.SingleOrDefault(entry =>
        entry.Edition == target.Edition &&
        entry.Architecture == target.Architecture &&
        string.Equals(entry.DisplayVersion, target.DisplayVersion, StringComparison.OrdinalIgnoreCase));

    private static WindowsCompatibilityEntry CreateEntry(WindowsEdition edition, string displayVersion, int baseBuild) => new(
        edition,
        WindowsArchitecture.Amd64,
        displayVersion,
        new WindowsBuildRange(baseBuild, baseBuild),
        InitialSupportedSettings,
        [new Uri(Windows11ReleaseInformationUrl)],
        new CompatibilityEvidence(IsDocumented: true, IsSchemaValidated: false, IsVmValidated: false));
}

public sealed class CatalogDeploymentCompatibilityValidator(
    IWindowsCompatibilityCatalog? catalog = null,
    IDeploymentCompatibilityValidator? baselineValidator = null) : IDeploymentCompatibilityValidator
{
    private readonly IWindowsCompatibilityCatalog _catalog = catalog ?? new Windows11CompatibilityCatalog();
    private readonly IDeploymentCompatibilityValidator _baselineValidator = baselineValidator ?? new BasicDeploymentCompatibilityValidator();

    public DeploymentCompatibilityResult Validate(WindowsDeploymentTarget target, ProvisioningProfile profile)
    {
        var baseline = _baselineValidator.Validate(target, profile);
        if (!baseline.CanContinue)
        {
            return baseline;
        }

        var warnings = baseline.Warnings.ToList();
        var entry = _catalog.Find(target);
        if (entry is null)
        {
            warnings.Add(new DeploymentValidationError(
                "deployment.target.version.unknown",
                "target.displayVersion",
                "Target Windows version is not in the documented compatibility catalog."));
        }
        else if (!entry.BuildRange.Contains(target.Build))
        {
            warnings.Add(new DeploymentValidationError(
                "deployment.target.build.unknown",
                "target.build",
                "Target Windows build is outside the documented compatibility catalog range."));
        }

        return new DeploymentCompatibilityResult(
            warnings.Count == 0 ? DeploymentCompatibilityState.Documented : DeploymentCompatibilityState.Warning,
            [],
            warnings);
    }
}

public sealed class DeploymentPreviewService(
    IDeploymentProfileValidator? deploymentProfileValidator = null,
    IProvisioningPlanBuilder? provisioningPlanBuilder = null) : IDeploymentPreviewService
{
    private readonly IDeploymentProfileValidator _deploymentProfileValidator = deploymentProfileValidator ?? new DeploymentProfileValidator();
    private readonly IProvisioningPlanBuilder _provisioningPlanBuilder = provisioningPlanBuilder ?? new ProvisioningPlanBuilder();

    public DeploymentPreviewResult CreatePreview(DeploymentPreparationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Profile);
        ArgumentNullException.ThrowIfNull(request.Target);

        var validation = _deploymentProfileValidator.Validate(request);
        if (!validation.IsValid)
        {
            return new DeploymentPreviewResult(
                null,
                validation.Errors,
                validation.Warnings);
        }

        var profilePlan = _provisioningPlanBuilder.Create(request.Profile);
        if (!profilePlan.IsValid)
        {
            return new DeploymentPreviewResult(
                null,
                profilePlan.Errors.Select(static error => new DeploymentValidationError(error.Code, error.FieldPath, error.Message)).ToArray(),
                validation.Warnings);
        }

        var operations = new[]
        {
            DeploymentOperationKind.GenerateAnswerFile,
            DeploymentOperationKind.CreateManifest,
            DeploymentOperationKind.CopyLocalPayload,
            DeploymentOperationKind.CopyAllowedInstaller,
        };
        return new DeploymentPreviewResult(
            new DeploymentPreview(
                request.Profile.ProfileId,
                request.Profile.Revision,
                request.Target,
                validation.CompatibilityState,
                profilePlan.Plan!.Steps.Where(static step => step.Kind != ProvisioningStepKind.RequestDomainJoin).ToArray(),
                profilePlan.Plan.RuntimePrompts.Where(static prompt => prompt.Kind != RuntimePromptKind.DomainJoin).ToArray(),
                operations,
                IsFileOnly: true),
            [],
            validation.Warnings);
    }
}

public sealed class DeploymentPackagePlanner : IDeploymentPackagePlanner
{
    public DeploymentPackagePlan CreatePlan(DeploymentPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (!preview.IsFileOnly)
        {
            throw new InvalidOperationException("Deployment package plans must remain file-only.");
        }

        return new DeploymentPackagePlan(
            preview,
            [
                new DeploymentPackageFile("autounattend.xml", "Windows Setup answer file"),
                new DeploymentPackageFile("deployment-manifest.json", "Package integrity manifest"),
                new DeploymentPackageFile("README.txt", "Deployment instructions"),
            ]);
    }
}
