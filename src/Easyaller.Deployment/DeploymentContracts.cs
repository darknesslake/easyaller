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
    WindowsDeploymentTarget Target);

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

        if (string.IsNullOrWhiteSpace(target.DisplayVersion))
        {
            warnings.Add(new DeploymentValidationError(
                "deployment.target.displayVersion.unknown",
                "target.displayVersion",
                "Target display version is unknown and requires later compatibility validation."));
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

public sealed class DeploymentPreviewService(
    IDeploymentCompatibilityValidator? compatibilityValidator = null,
    IProvisioningPlanBuilder? provisioningPlanBuilder = null) : IDeploymentPreviewService
{
    private readonly IDeploymentCompatibilityValidator _compatibilityValidator = compatibilityValidator ?? new BasicDeploymentCompatibilityValidator();
    private readonly IProvisioningPlanBuilder _provisioningPlanBuilder = provisioningPlanBuilder ?? new ProvisioningPlanBuilder();

    public DeploymentPreviewResult CreatePreview(DeploymentPreparationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Profile);
        ArgumentNullException.ThrowIfNull(request.Target);

        var profilePlan = _provisioningPlanBuilder.Create(request.Profile);
        if (!profilePlan.IsValid)
        {
            return new DeploymentPreviewResult(
                null,
                profilePlan.Errors.Select(static error => new DeploymentValidationError(error.Code, error.FieldPath, error.Message)).ToArray(),
                []);
        }

        var compatibility = _compatibilityValidator.Validate(request.Target, request.Profile);
        if (!compatibility.CanContinue)
        {
            return new DeploymentPreviewResult(null, compatibility.Errors, compatibility.Warnings);
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
                compatibility.State,
                profilePlan.Plan!.Steps,
                profilePlan.Plan.RuntimePrompts,
                operations,
                IsFileOnly: true),
            [],
            compatibility.Warnings);
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
