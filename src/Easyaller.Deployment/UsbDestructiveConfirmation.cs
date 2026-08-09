namespace Easyaller.Deployment;

public enum UsbDestructiveConfirmationStatus
{
    AwaitingTypedPhrase,
    Confirmed,
    Consumed,
    Expired,
    Blocked,
}

public sealed record UsbDestructiveConfirmationPrompt(
    DiskSelection Selection,
    string Vendor,
    string SerialOrDeviceId,
    int DiskNumber,
    long SizeBytes,
    string RequiredPhrase,
    DateTimeOffset ExpiresAt);

public sealed class UsbDestructiveConfirmation
{
    internal UsbDestructiveConfirmation(Guid id, UsbDestructiveConfirmationPrompt prompt)
    {
        Id = id;
        Prompt = prompt;
    }

    internal Guid Id { get; }

    public UsbDestructiveConfirmationPrompt Prompt { get; }

    public UsbDestructiveConfirmationStatus Status { get; internal set; } = UsbDestructiveConfirmationStatus.AwaitingTypedPhrase;
}

public sealed record UsbDestructiveConfirmationBeginResult(
    UsbDestructiveConfirmation? Confirmation,
    UsbDestructiveConfirmationStatus? StatusAtResult,
    IReadOnlyList<DeploymentValidationError> Errors)
{
    public bool IsAwaitingTypedPhrase =>
        StatusAtResult == UsbDestructiveConfirmationStatus.AwaitingTypedPhrase && Errors.Count == 0;
}

public sealed record UsbDestructiveConfirmationResult(
    UsbDestructiveConfirmation Confirmation,
    UsbDestructiveConfirmationStatus StatusAtResult,
    DiskInventoryItem? CurrentDisk,
    IReadOnlyList<DeploymentValidationError> Errors)
{
    public bool IsConfirmed => StatusAtResult == UsbDestructiveConfirmationStatus.Confirmed && Errors.Count == 0;

    public bool IsAuthorizedForFirstWrite => StatusAtResult == UsbDestructiveConfirmationStatus.Consumed && CurrentDisk is not null && Errors.Count == 0;
}

public sealed class UsbDestructiveConfirmationStateMachine
{
    public const string RequiredPhrase = "ERASE";
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(5);

    private readonly object _sync = new();
    private readonly Dictionary<Guid, UsbDestructiveConfirmation> _confirmations = [];
    private readonly RemovableDiskSafetyService _diskSafety;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _lifetime;

    public UsbDestructiveConfirmationStateMachine(
        RemovableDiskSafetyService? diskSafety = null,
        TimeProvider? timeProvider = null,
        TimeSpan? lifetime = null)
    {
        _diskSafety = diskSafety ?? new RemovableDiskSafetyService();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lifetime = lifetime ?? DefaultLifetime;
        if (_lifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime), "Confirmation lifetime must be positive.");
        }
    }

    public UsbDestructiveConfirmationBeginResult Begin(DiskSelectionVerification selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (!selection.IsStillEligible || selection.Assessment is null)
        {
            return new UsbDestructiveConfirmationBeginResult(null, null, selection.Errors.Count > 0
                ? selection.Errors
                : [Error("usb.confirmation.selection.ineligible", "selection", "Only an explicitly selected eligible removable disk can be confirmed.")]);
        }

        var disk = selection.Assessment.Disk;
        var prompt = new UsbDestructiveConfirmationPrompt(
            selection.Selection,
            string.IsNullOrWhiteSpace(disk.Vendor) ? "Unknown vendor" : disk.Vendor,
            string.IsNullOrWhiteSpace(disk.Identity.SerialNumber) ? disk.Identity.UniqueId : disk.Identity.SerialNumber,
            disk.DiskNumber,
            disk.SizeBytes,
            RequiredPhrase,
            _timeProvider.GetUtcNow().Add(_lifetime));
        var confirmation = new UsbDestructiveConfirmation(Guid.NewGuid(), prompt);
        lock (_sync)
        {
            _confirmations.Add(confirmation.Id, confirmation);
        }

        return new UsbDestructiveConfirmationBeginResult(confirmation, confirmation.Status, []);
    }

    public UsbDestructiveConfirmationResult Submit(UsbDestructiveConfirmation confirmation, string? typedPhrase)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        lock (_sync)
        {
            if (!TryGetActive(confirmation, out var errors))
            {
                return Result(confirmation, null, errors);
            }

            if (IsExpired(confirmation))
            {
                confirmation.Status = UsbDestructiveConfirmationStatus.Expired;
                return Result(confirmation, null, [ExpiredError()]);
            }

            if (confirmation.Status != UsbDestructiveConfirmationStatus.AwaitingTypedPhrase)
            {
                return Result(confirmation, null, [Error("usb.confirmation.state.invalid", "confirmation", "Confirmation is no longer awaiting the erase phrase.")]);
            }

            if (!string.Equals(typedPhrase, RequiredPhrase, StringComparison.Ordinal))
            {
                return Result(confirmation, null, [Error("usb.confirmation.phrase.invalid", "typedPhrase", "Type the exact uppercase phrase ERASE to continue.")]);
            }

            confirmation.Status = UsbDestructiveConfirmationStatus.Confirmed;
            return Result(confirmation, null, []);
        }
    }

    public UsbDestructiveConfirmationResult AuthorizeFirstWrite(
        UsbDestructiveConfirmation confirmation,
        IReadOnlyList<DiskInventoryItem> currentDisks)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        ArgumentNullException.ThrowIfNull(currentDisks);
        lock (_sync)
        {
            if (!TryGetActive(confirmation, out var errors))
            {
                return Result(confirmation, null, errors);
            }

            if (IsExpired(confirmation))
            {
                confirmation.Status = UsbDestructiveConfirmationStatus.Expired;
                return Result(confirmation, null, [ExpiredError()]);
            }

            if (confirmation.Status != UsbDestructiveConfirmationStatus.Confirmed)
            {
                return Result(confirmation, null, [Error("usb.confirmation.notConfirmed", "confirmation", "The exact erase phrase must be accepted before a write can be authorized.")]);
            }

            var recheck = _diskSafety.Recheck(confirmation.Prompt.Selection, currentDisks);
            if (!recheck.IsStillEligible || recheck.Assessment is null)
            {
                confirmation.Status = UsbDestructiveConfirmationStatus.Blocked;
                return Result(confirmation, null, recheck.Errors.Count > 0
                    ? recheck.Errors
                    : [Error("usb.confirmation.recheck.failed", "confirmation", "Selected disk cannot be rechecked for the first write.")]);
            }

            confirmation.Status = UsbDestructiveConfirmationStatus.Consumed;
            return Result(confirmation, recheck.Assessment.Disk, []);
        }
    }

    private bool TryGetActive(UsbDestructiveConfirmation confirmation, out IReadOnlyList<DeploymentValidationError> errors)
    {
        if (!_confirmations.TryGetValue(confirmation.Id, out var registered) || !ReferenceEquals(registered, confirmation))
        {
            errors = [Error("usb.confirmation.unknown", "confirmation", "Confirmation does not belong to this state machine.")];
            return false;
        }

        errors = [];
        return true;
    }

    private bool IsExpired(UsbDestructiveConfirmation confirmation) => _timeProvider.GetUtcNow() >= confirmation.Prompt.ExpiresAt;

    private static UsbDestructiveConfirmationResult Result(
        UsbDestructiveConfirmation confirmation,
        DiskInventoryItem? disk,
        IReadOnlyList<DeploymentValidationError> errors) =>
        new(confirmation, confirmation.Status, disk, errors);

    private static DeploymentValidationError ExpiredError() =>
        Error("usb.confirmation.expired", "confirmation", "Confirmation expired before the first write was authorized.");

    private static DeploymentValidationError Error(string code, string fieldPath, string message) => new(code, fieldPath, message);
}
