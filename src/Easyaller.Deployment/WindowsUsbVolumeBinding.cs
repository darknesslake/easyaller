using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Easyaller.Deployment;

public sealed record UsbVolumeBindingResult(
    string RootDirectory,
    DiskInventoryItem? Disk,
    IReadOnlyList<DeploymentValidationError> Errors)
{
    public bool IsResolved => Disk is not null && Errors.Count == 0;
}

public interface IUsbVolumeBindingInspector
{
    UsbVolumeBindingResult Inspect(string rootDirectory);
}

public sealed class WindowsUsbVolumeBindingInspector : IUsbVolumeBindingInspector
{
    private const string Query = "& { $ErrorActionPreference='Stop'; $root=$env:EASYALLER_USB_ROOT; if([string]::IsNullOrWhiteSpace($root)){throw 'Easyaller USB root was not supplied.'}; $volume=Get-Volume -FilePath $root -ErrorAction Stop; $partitions=@($volume | Get-Partition -ErrorAction Stop); if($partitions.Count -ne 1){throw 'The USB root did not resolve to exactly one partition.'}; $disk=Get-Disk -Number $partitions[0].DiskNumber -ErrorAction Stop; $drive=Get-CimInstance Win32_DiskDrive | Where-Object { [int]$_.Index -eq [int]$disk.Number } | Select-Object -First 1; [pscustomobject]@{ DiskNumber=$disk.Number; UniqueId=$disk.UniqueId; SerialNumber=$disk.SerialNumber; FriendlyName=$disk.FriendlyName; BusType=[string]$disk.BusType; Size=$disk.Size; IsSystem=$disk.IsSystem; IsBoot=$disk.IsBoot; IsReadOnly=$disk.IsReadOnly; IsOffline=$disk.IsOffline; IsRemovable=if($null -eq $drive){$false}else{[string]$drive.MediaType -match 'Removable'} } | ConvertTo-Json -Compress }";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public UsbVolumeBindingResult Inspect(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory) || !Path.IsPathFullyQualified(rootDirectory))
        {
            return Failure(rootDirectory, "usb.volume.path.invalid", "USB root must be an absolute path.");
        }

        if (!OperatingSystem.IsWindows())
        {
            return Failure(rootDirectory, "usb.volume.platform.unsupported", "USB volume binding is available only on Windows.");
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
            process.StartInfo.Environment["EASYALLER_USB_ROOT"] = rootDirectory;
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
                return Failure(rootDirectory, "usb.volume.inspect.failed", string.IsNullOrWhiteSpace(error) ? "USB volume inspection failed." : error.Trim());
            }

            var probe = JsonSerializer.Deserialize<VolumeProbe>(output, JsonOptions);
            if (probe is null)
            {
                return Failure(rootDirectory, "usb.volume.inspect.failed", "USB volume inspection produced no binding.");
            }

            return new UsbVolumeBindingResult(rootDirectory, probe.ToDisk(), []);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or JsonException)
        {
            return Failure(rootDirectory, "usb.volume.inspect.failed", exception.Message);
        }
    }

    private static UsbVolumeBindingResult Failure(string rootDirectory, string code, string message) =>
        new(rootDirectory, null, [new DeploymentValidationError(code, "usbRootDirectory", message)]);

    private sealed record VolumeProbe(
        int DiskNumber,
        string? UniqueId,
        string? SerialNumber,
        string? FriendlyName,
        string? BusType,
        long Size,
        bool IsSystem,
        bool IsBoot,
        bool IsReadOnly,
        bool IsOffline,
        bool IsRemovable)
    {
        public DiskInventoryItem ToDisk() => new(
            new DiskIdentity(UniqueId ?? string.Empty, SerialNumber),
            DiskNumber,
            FriendlyName ?? string.Empty,
            null,
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

public sealed class DiskBoundDirectoryUsbMediaWriteTarget : IUsbMediaWriteTarget
{
    private readonly string _rootDirectory;
    private readonly IUsbVolumeBindingInspector _bindingInspector;
    private readonly RemovableDiskSafetyService _diskSafety;

    public DiskBoundDirectoryUsbMediaWriteTarget(
        string rootDirectory,
        IUsbVolumeBindingInspector? bindingInspector = null,
        RemovableDiskSafetyService? diskSafety = null)
    {
        _rootDirectory = Path.GetFullPath(rootDirectory);
        _bindingInspector = bindingInspector ?? new WindowsUsbVolumeBindingInspector();
        _diskSafety = diskSafety ?? new RemovableDiskSafetyService();
    }

    public IUsbMediaWriteSession Open(DiskInventoryItem authorizedDisk, UsbMediaWritePlan plan)
    {
        ArgumentNullException.ThrowIfNull(authorizedDisk);
        ArgumentNullException.ThrowIfNull(plan);
        var binding = _bindingInspector.Inspect(_rootDirectory);
        if (!binding.IsResolved || binding.Disk is null)
        {
            throw new InvalidOperationException("Selected USB root could not be bound to a disk identity.");
        }

        if (!string.Equals(Path.GetFullPath(binding.RootDirectory), _rootDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("USB volume binding did not return the requested root directory.");
        }

        var planRecheck = _diskSafety.Recheck(plan.Selection, [binding.Disk]);
        if (!planRecheck.IsStillEligible || !SameIdentity(authorizedDisk, binding.Disk))
        {
            throw new InvalidOperationException("USB root does not belong to the currently authorized removable disk.");
        }

        if (!Directory.Exists(_rootDirectory) || Directory.EnumerateFileSystemEntries(_rootDirectory).Any())
        {
            throw new InvalidOperationException("USB root must be an existing empty volume root before writing starts.");
        }

        var stagingDirectory = Path.Combine(_rootDirectory, ".easyaller-staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDirectory);
        return new DirectoryUsbMediaWriteSession(_rootDirectory, stagingDirectory);
    }

    private static bool SameIdentity(DiskInventoryItem authorized, DiskInventoryItem bound) =>
        string.Equals(authorized.Identity.UniqueId, bound.Identity.UniqueId, StringComparison.Ordinal) &&
        (string.IsNullOrWhiteSpace(authorized.Identity.SerialNumber) ||
         string.Equals(authorized.Identity.SerialNumber, bound.Identity.SerialNumber, StringComparison.Ordinal));

    private sealed class DirectoryUsbMediaWriteSession : IUsbMediaWriteSession
    {
        private readonly string _rootDirectory;
        private readonly string _stagingDirectory;
        private bool _committed;
        private bool _aborted;

        public DirectoryUsbMediaWriteSession(string rootDirectory, string stagingDirectory)
        {
            _rootDirectory = rootDirectory;
            _stagingDirectory = stagingDirectory;
        }

        public void Write(UsbMediaWriteFile file, Stream contents)
        {
            if (_committed || _aborted)
            {
                throw new InvalidOperationException("USB write session is no longer writable.");
            }

            var destination = GetContainedPath(_stagingDirectory, file.DestinationRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            contents.CopyTo(output);
        }

        public void Commit()
        {
            if (_committed || _aborted)
            {
                throw new InvalidOperationException("USB write session cannot be committed.");
            }

            var unexpectedEntries = Directory.EnumerateFileSystemEntries(_rootDirectory)
                .Where(entry => !string.Equals(entry, _stagingDirectory, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (unexpectedEntries.Length > 0)
            {
                throw new IOException("USB root changed while files were staged.");
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(_stagingDirectory))
            {
                var destination = Path.Combine(_rootDirectory, Path.GetFileName(entry));
                if (Directory.Exists(entry))
                {
                    Directory.Move(entry, destination);
                }
                else
                {
                    File.Move(entry, destination);
                }
            }

            Directory.Delete(_stagingDirectory, recursive: true);
            _committed = true;
        }

        public UsbMediaWriteFileVerification ReadFinal(string destinationRelativePath)
        {
            if (!_committed)
            {
                throw new InvalidOperationException("USB write session has not committed its files.");
            }

            var path = GetContainedPath(_rootDirectory, destinationRelativePath);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return new UsbMediaWriteFileVerification(stream.Length, Convert.ToHexString(SHA256.HashData(stream)));
        }

        public void Abort()
        {
            if (!_committed && !_aborted && Directory.Exists(_stagingDirectory))
            {
                Directory.Delete(_stagingDirectory, recursive: true);
            }

            _aborted = true;
        }

        public void Dispose()
        {
        }

        private static string GetContainedPath(string rootDirectory, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) ||
                relativePath.Split(['/', '\\']).Any(static segment => segment is "" or "." or ".."))
            {
                throw new InvalidOperationException("USB destination path is unsafe.");
            }

            var root = Path.GetFullPath(rootDirectory);
            var path = Path.GetFullPath(Path.Combine(root, relativePath));
            var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            return path.StartsWith(rootWithSeparator, StringComparison.Ordinal)
                ? path
                : throw new InvalidOperationException("USB destination path escaped its root.");
        }
    }
}
