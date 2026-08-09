using System.Diagnostics;
using System.Text.Json;

namespace Easyaller.Deployment;

public enum DiskBusType
{
    Usb,
    Sd,
    Mmc,
    Other,
}

public sealed record DiskIdentity(string UniqueId, string? SerialNumber)
{
    public bool IsUsable => !string.IsNullOrWhiteSpace(UniqueId);
}

public sealed record DiskInventoryItem(
    DiskIdentity Identity,
    int DiskNumber,
    string FriendlyName,
    string? Vendor,
    DiskBusType BusType,
    bool IsRemovable,
    long SizeBytes,
    bool IsSystem,
    bool IsBoot,
    bool IsReadOnly,
    bool IsOffline);

public sealed record DiskInventoryResult(
    IReadOnlyList<DiskInventoryItem> Disks,
    IReadOnlyList<DeploymentValidationError> Errors)
{
    public bool IsAvailable => Errors.Count == 0;
}

public enum DiskSafetyStatus
{
    EligibleForConfirmation,
    Blocked,
}

public sealed record DiskSafetyAssessment(
    DiskInventoryItem Disk,
    DiskSafetyStatus Status,
    IReadOnlyList<DeploymentValidationError> Errors)
{
    public bool IsEligibleForConfirmation => Status == DiskSafetyStatus.EligibleForConfirmation && Errors.Count == 0;
}

public sealed record DiskSelection(DiskIdentity Identity, int DiskNumberAtSelection);

public sealed record DiskSelectionVerification(
    DiskSelection Selection,
    DiskSafetyAssessment? Assessment,
    IReadOnlyList<DeploymentValidationError> Errors)
{
    public bool IsStillEligible => Assessment?.IsEligibleForConfirmation == true && Errors.Count == 0;
}

public interface IRemovableDiskInventoryProvider
{
    DiskInventoryResult Read();
}

public sealed class RemovableDiskSafetyService
{
    public IReadOnlyList<DiskSafetyAssessment> Assess(IReadOnlyList<DiskInventoryItem> disks)
    {
        ArgumentNullException.ThrowIfNull(disks);
        return disks.Select(Assess).ToArray();
    }

    public DiskSelectionVerification Select(DiskInventoryItem disk)
    {
        ArgumentNullException.ThrowIfNull(disk);
        var assessment = Assess(disk);
        return assessment.IsEligibleForConfirmation
            ? new DiskSelectionVerification(
                new DiskSelection(disk.Identity, disk.DiskNumber),
                assessment,
                [])
            : new DiskSelectionVerification(
                new DiskSelection(disk.Identity, disk.DiskNumber),
                assessment,
                assessment.Errors);
    }

    public DiskSelectionVerification Recheck(DiskSelection selection, IReadOnlyList<DiskInventoryItem> currentDisks)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(currentDisks);

        var matchingDisks = currentDisks
            .Where(disk => string.Equals(disk.Identity.UniqueId, selection.Identity.UniqueId, StringComparison.Ordinal))
            .ToArray();
        if (matchingDisks.Length != 1)
        {
            return MissingSelection(selection, "Selected disk is no longer uniquely present in the current inventory.");
        }

        var disk = matchingDisks[0];
        if (!string.IsNullOrWhiteSpace(selection.Identity.SerialNumber) &&
            !string.Equals(selection.Identity.SerialNumber, disk.Identity.SerialNumber, StringComparison.Ordinal))
        {
            return MissingSelection(selection, "Selected disk serial number changed after selection.");
        }

        var assessment = Assess(disk);
        return assessment.IsEligibleForConfirmation
            ? new DiskSelectionVerification(selection, assessment, [])
            : new DiskSelectionVerification(selection, assessment, assessment.Errors);
    }

    private static DiskSafetyAssessment Assess(DiskInventoryItem disk)
    {
        var errors = new List<DeploymentValidationError>();
        if (!disk.Identity.IsUsable)
        {
            errors.Add(Error("usb.disk.identity.missing", "disk.identity.uniqueId", "Disk does not have a usable immutable identity."));
        }

        if (!disk.IsRemovable)
        {
            errors.Add(Error("usb.disk.removable.required", "disk.isRemovable", "Only a disk reported as removable can be considered."));
        }

        if (disk.BusType is not (DiskBusType.Usb or DiskBusType.Sd or DiskBusType.Mmc))
        {
            errors.Add(Error("usb.disk.bus.unsupported", "disk.busType", "Only USB, SD, or MMC bus types can be considered."));
        }

        if (disk.IsSystem || disk.IsBoot)
        {
            errors.Add(Error("usb.disk.system.prohibited", "disk", "System and boot disks are never eligible."));
        }

        if (disk.IsReadOnly || disk.IsOffline)
        {
            errors.Add(Error("usb.disk.state.unsuitable", "disk", "Read-only or offline disks are not eligible."));
        }

        if (disk.SizeBytes < 1)
        {
            errors.Add(Error("usb.disk.size.invalid", "disk.sizeBytes", "Disk size must be positive."));
        }

        return new DiskSafetyAssessment(
            disk,
            errors.Count == 0 ? DiskSafetyStatus.EligibleForConfirmation : DiskSafetyStatus.Blocked,
            errors);
    }

    private static DiskSelectionVerification MissingSelection(DiskSelection selection, string message) =>
        new(
            selection,
            null,
            [Error("usb.disk.selection.changed", "selection.identity", message)]);

    private static DeploymentValidationError Error(string code, string path, string message) => new(code, path, message);
}

public sealed class WindowsRemovableDiskInventoryProvider : IRemovableDiskInventoryProvider
{
    private const string Query = "& { $ErrorActionPreference='Stop'; $drives=@{}; Get-CimInstance Win32_DiskDrive | ForEach-Object { $drives[[int]$_.Index]=$_ }; Get-Disk | ForEach-Object { $drive=$drives[[int]$_.Number]; [pscustomobject]@{ Number=$_.Number; FriendlyName=$_.FriendlyName; Vendor=if($null -eq $drive){$null}else{$drive.Manufacturer}; SerialNumber=$_.SerialNumber; UniqueId=$_.UniqueId; BusType=[string]$_.BusType; Size=$_.Size; IsBoot=$_.IsBoot; IsSystem=$_.IsSystem; IsReadOnly=$_.IsReadOnly; IsOffline=$_.IsOffline; IsRemovable=if($null -eq $drive){$false}else{[string]$drive.MediaType -match 'Removable'} } | ConvertTo-Json -Compress } }";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public DiskInventoryResult Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Failure("usb.inventory.platform.unsupported", "Disk inventory is available only on Windows.");
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };
            process.StartInfo.ArgumentList.Add("-NoLogo");
            process.StartInfo.ArgumentList.Add("-NoProfile");
            process.StartInfo.ArgumentList.Add("-NonInteractive");
            process.StartInfo.ArgumentList.Add("-Command");
            process.StartInfo.ArgumentList.Add(Query);
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                return Failure("usb.inventory.read.failed", string.IsNullOrWhiteSpace(error) ? "Windows disk inventory failed." : error.Trim());
            }

            var disks = output
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => JsonSerializer.Deserialize<DiskProbe>(line, JsonOptions))
                .Where(static disk => disk is not null)
                .Select(static disk => disk!.ToInventoryItem())
                .OrderBy(static disk => disk.DiskNumber)
                .ToArray();
            return new DiskInventoryResult(disks, []);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or JsonException)
        {
            return Failure("usb.inventory.read.failed", exception.Message);
        }
    }

    private static DiskInventoryResult Failure(string code, string message) =>
        new([], [new DeploymentValidationError(code, "diskInventory", message)]);

    private sealed record DiskProbe(
        int Number,
        string? FriendlyName,
        string? Vendor,
        string? SerialNumber,
        string? UniqueId,
        string? BusType,
        long Size,
        bool IsBoot,
        bool IsSystem,
        bool IsReadOnly,
        bool IsOffline,
        bool IsRemovable)
    {
        public DiskInventoryItem ToInventoryItem() => new(
            new DiskIdentity(UniqueId ?? string.Empty, SerialNumber),
            Number,
            FriendlyName ?? string.Empty,
            Vendor,
            BusType switch
            {
                "USB" => DiskBusType.Usb,
                "SD" => DiskBusType.Sd,
                "MMC" => DiskBusType.Mmc,
                _ => DiskBusType.Other,
            },
            IsRemovable,
            Size,
            IsSystem,
            IsBoot,
            IsReadOnly,
            IsOffline);
    }
}
