using System.Reflection;
using System.Security.Cryptography;
using Easyaller.Deployment;

namespace Easyaller.Core.Tests;

public sealed class WindowsUsbVolumeBindingTests
{
    [Fact]
    public void Execute_BoundEmptyRootAndAuthorizedDisk_WritesVerifiedFilesThroughStaging()
    {
        using var directory = new TemporaryDirectory();
        var root = Path.Combine(directory.Path, "usb-root");
        Directory.CreateDirectory(root);
        var sourcePath = Path.Combine(directory.Path, "setup.exe");
        File.WriteAllText(sourcePath, "setup");
        var disk = UsbDisk("usb-001", 3);
        var plan = Plan(disk, sourcePath);
        var target = new DiskBoundDirectoryUsbMediaWriteTarget(root, new FakeBindingInspector(root, disk));

        var result = new UsbMediaWriteEngine().Execute(plan, Authorize(disk), target);

        Assert.True(result.IsReady);
        Assert.Equal("setup", File.ReadAllText(Path.Combine(root, "setup.exe")));
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(root), entry => Path.GetFileName(entry).StartsWith(".easyaller-staging-", StringComparison.Ordinal));
    }

    [Fact]
    public void Execute_BoundDifferentDisk_BlocksBeforeCreatingStagingDirectory()
    {
        using var directory = new TemporaryDirectory();
        var root = Path.Combine(directory.Path, "usb-root");
        Directory.CreateDirectory(root);
        var sourcePath = Path.Combine(directory.Path, "setup.exe");
        File.WriteAllText(sourcePath, "setup");
        var disk = UsbDisk("usb-001", 3);
        var target = new DiskBoundDirectoryUsbMediaWriteTarget(root, new FakeBindingInspector(root, UsbDisk("usb-replacement", 3)));

        var result = new UsbMediaWriteEngine().Execute(Plan(disk, sourcePath), Authorize(disk), target);

        Assert.False(result.IsReady);
        Assert.Contains(result.Errors, error => error.Code == "usb.write.failed");
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }

    [Fact]
    public void Execute_NonEmptyBoundRoot_BlocksWithoutReplacingExistingFiles()
    {
        using var directory = new TemporaryDirectory();
        var root = Path.Combine(directory.Path, "usb-root");
        Directory.CreateDirectory(root);
        var sentinel = Path.Combine(root, "keep.txt");
        File.WriteAllText(sentinel, "keep");
        var sourcePath = Path.Combine(directory.Path, "setup.exe");
        File.WriteAllText(sourcePath, "setup");
        var disk = UsbDisk("usb-001", 3);
        var target = new DiskBoundDirectoryUsbMediaWriteTarget(root, new FakeBindingInspector(root, disk));

        var result = new UsbMediaWriteEngine().Execute(Plan(disk, sourcePath), Authorize(disk), target);

        Assert.False(result.IsReady);
        Assert.Equal("keep", File.ReadAllText(sentinel));
        Assert.False(File.Exists(Path.Combine(root, "setup.exe")));
    }

    [Fact]
    public void WindowsInspector_UsesReadOnlyStorageQueriesOnly()
    {
        var script = (string?)typeof(WindowsUsbVolumeBindingInspector)
            .GetField("Query", BindingFlags.Static | BindingFlags.NonPublic)
            ?.GetRawConstantValue();

        Assert.NotNull(script);
        Assert.Contains("Get-Volume -FilePath", script!, StringComparison.Ordinal);
        Assert.Contains("Get-Partition", script, StringComparison.Ordinal);
        Assert.Contains("Get-Disk", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Format-Volume", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Clear-Disk", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Set-Disk", script, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsRootResolver_RequiresExactlyOneDriveLetterWithoutDiskMutation()
    {
        var script = (string?)typeof(WindowsUsbVolumeRootResolver)
            .GetField("Query", BindingFlags.Static | BindingFlags.NonPublic)
            ?.GetRawConstantValue();

        Assert.NotNull(script);
        Assert.Contains("Get-Disk -Number", script!, StringComparison.Ordinal);
        Assert.Contains("Get-Partition -DiskNumber", script, StringComparison.Ordinal);
        Assert.Contains("$partitions.Count -ne 1", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Format-Volume", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Clear-Disk", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Set-Disk", script, StringComparison.Ordinal);
    }

    private static UsbMediaWritePlan Plan(DiskInventoryItem disk, string sourcePath) => new(
        new DiskSelection(disk.Identity, disk.DiskNumber),
        [new UsbMediaWriteFile(
            UsbMediaWriteFileSource.WindowsSetup,
            sourcePath,
            "setup.exe",
            new FileInfo(sourcePath).Length,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourcePath))))]);

    private static UsbDestructiveConfirmationResult Authorize(DiskInventoryItem disk)
    {
        var safety = new RemovableDiskSafetyService();
        var machine = new UsbDestructiveConfirmationStateMachine(safety);
        var confirmation = machine.Begin(safety.Select(disk)).Confirmation!;
        machine.Submit(confirmation, "ERASE");
        return machine.AuthorizeFirstWrite(confirmation, [disk]);
    }

    private static DiskInventoryItem UsbDisk(string uniqueId, int number) => new(
        new DiskIdentity(uniqueId, "serial-" + uniqueId),
        number,
        "Test USB",
        "Easyaller Tests",
        DiskBusType.Usb,
        IsRemovable: true,
        SizeBytes: 64L * 1024 * 1024 * 1024,
        IsSystem: false,
        IsBoot: false,
        IsReadOnly: false,
        IsOffline: false);

    private sealed class FakeBindingInspector(string root, DiskInventoryItem disk) : IUsbVolumeBindingInspector
    {
        public UsbVolumeBindingResult Inspect(string rootDirectory) => new(root, disk, []);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "easyaller-tests-" + Guid.NewGuid());
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
