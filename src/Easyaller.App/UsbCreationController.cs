using Easyaller.Deployment;

namespace Easyaller.App;

public sealed record UsbCreatorCandidate(DiskInventoryItem Disk)
{
    public string Name => string.IsNullOrWhiteSpace(Disk.FriendlyName) ? "Съёмный диск" : Disk.FriendlyName;

    public string Detail => string.Join(" · ",
        new[]
        {
            Disk.Vendor,
            string.IsNullOrWhiteSpace(Disk.Identity.SerialNumber) ? Disk.Identity.UniqueId : Disk.Identity.SerialNumber,
            $"Диск {Disk.DiskNumber}",
            FormatSize(Disk.SizeBytes),
        }.Where(static value => !string.IsNullOrWhiteSpace(value)));

    private static string FormatSize(long sizeBytes) => $"{sizeBytes / (1024d * 1024 * 1024):0.##} GiB";
}

public sealed record UsbCreatorInventoryResult(
    IReadOnlyList<UsbCreatorCandidate> Candidates,
    IReadOnlyList<DeploymentValidationError> Errors);

public sealed record UsbCreatorPreparationResult(
    UsbMediaWritePlan? Plan,
    UsbDestructiveConfirmation? Confirmation,
    IReadOnlyList<DeploymentValidationError> Errors)
{
    public bool IsReadyForPhrase => Plan is not null && Confirmation is not null && Errors.Count == 0;
}

public sealed record UsbCreatorWriteResult(
    UsbMediaWriteExecutionResult? Execution,
    IReadOnlyList<DeploymentValidationError> Errors)
{
    public bool IsReady => Execution?.IsReady == true && Errors.Count == 0;
}

public sealed class UsbCreationController
{
    private readonly IRemovableDiskInventoryProvider _inventoryProvider;
    private readonly RemovableDiskSafetyService _diskSafety;
    private readonly UsbDestructiveConfirmationStateMachine _confirmationStateMachine;
    private readonly UsbMediaWriteEngine _writeEngine;
    private readonly IUsbVolumeRootResolver _volumeRootResolver;
    private readonly Func<string, IUsbMediaWriteTarget> _targetFactory;

    public UsbCreationController(
        IRemovableDiskInventoryProvider? inventoryProvider = null,
        RemovableDiskSafetyService? diskSafety = null,
        UsbDestructiveConfirmationStateMachine? confirmationStateMachine = null,
        UsbMediaWriteEngine? writeEngine = null,
        IUsbVolumeRootResolver? volumeRootResolver = null,
        Func<string, IUsbMediaWriteTarget>? targetFactory = null)
    {
        _inventoryProvider = inventoryProvider ?? new WindowsRemovableDiskInventoryProvider();
        _diskSafety = diskSafety ?? new RemovableDiskSafetyService();
        _confirmationStateMachine = confirmationStateMachine ?? new UsbDestructiveConfirmationStateMachine(_diskSafety);
        _writeEngine = writeEngine ?? new UsbMediaWriteEngine();
        _volumeRootResolver = volumeRootResolver ?? new WindowsUsbVolumeRootResolver();
        _targetFactory = targetFactory ?? (root => new DiskBoundDirectoryUsbMediaWriteTarget(root));
    }

    public UsbCreatorInventoryResult RefreshCandidates()
    {
        var inventory = _inventoryProvider.Read();
        if (!inventory.IsAvailable)
        {
            return new UsbCreatorInventoryResult([], inventory.Errors);
        }

        var candidates = _diskSafety.Assess(inventory.Disks)
            .Where(static assessment => assessment.IsEligibleForConfirmation)
            .Select(static assessment => new UsbCreatorCandidate(assessment.Disk))
            .OrderBy(static candidate => candidate.Disk.DiskNumber)
            .ToArray();
        return new UsbCreatorInventoryResult(candidates, []);
    }

    public UsbCreatorPreparationResult Prepare(
        DiskInventoryItem selectedDisk,
        string setupMediaDirectory,
        string deploymentPackageDirectory)
    {
        ArgumentNullException.ThrowIfNull(selectedDisk);
        var selection = _diskSafety.Select(selectedDisk);
        if (!selection.IsStillEligible)
        {
            return new UsbCreatorPreparationResult(null, null, selection.Errors);
        }

        var plan = _writeEngine.CreatePlan(new UsbMediaWritePlanRequest(
            selection,
            setupMediaDirectory,
            deploymentPackageDirectory));
        if (!plan.IsReadyForAuthorizedWrite)
        {
            return new UsbCreatorPreparationResult(null, null, plan.Errors);
        }

        var confirmation = _confirmationStateMachine.Begin(selection);
        return confirmation.IsAwaitingTypedPhrase
            ? new UsbCreatorPreparationResult(plan.Plan, confirmation.Confirmation, [])
            : new UsbCreatorPreparationResult(null, null, confirmation.Errors);
    }

    public UsbCreatorWriteResult Write(UsbMediaWritePlan plan, UsbDestructiveConfirmation confirmation, string? typedPhrase)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(confirmation);
        var phrase = _confirmationStateMachine.Submit(confirmation, typedPhrase);
        if (!phrase.IsConfirmed)
        {
            return new UsbCreatorWriteResult(null, phrase.Errors);
        }

        var inventory = _inventoryProvider.Read();
        if (!inventory.IsAvailable)
        {
            return new UsbCreatorWriteResult(null, inventory.Errors);
        }

        var authorization = _confirmationStateMachine.AuthorizeFirstWrite(confirmation, inventory.Disks);
        if (!authorization.IsAuthorizedForFirstWrite || authorization.CurrentDisk is null)
        {
            return new UsbCreatorWriteResult(null, authorization.Errors);
        }

        var root = _volumeRootResolver.Resolve(authorization.CurrentDisk);
        if (!root.IsResolved || root.RootDirectory is null)
        {
            return new UsbCreatorWriteResult(null, root.Errors);
        }

        var execution = _writeEngine.Execute(plan, authorization, _targetFactory(root.RootDirectory));
        return new UsbCreatorWriteResult(execution, execution.Errors);
    }
}
