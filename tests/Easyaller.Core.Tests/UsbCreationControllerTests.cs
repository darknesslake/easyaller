using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Easyaller.App;
using Easyaller.Core.Profiles;
using Easyaller.Deployment;

namespace Easyaller.Core.Tests;

public sealed class UsbCreationControllerTests
{
    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    [Fact]
    public void RefreshCandidates_HidesSystemAndFixedDisks()
    {
        var eligible = UsbDisk("usb-001", 3);
        var system = UsbDisk("internal-001", 0) with
        {
            IsSystem = true,
            IsBoot = true,
            IsRemovable = false,
            BusType = DiskBusType.Other,
        };
        var controller = new UsbCreationController(inventoryProvider: new FakeInventory([eligible, system]));

        var result = controller.RefreshCandidates();

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("usb-001", candidate.Disk.Identity.UniqueId);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Prepare_ValidSourcesAndSelectedDisk_CreatesOneTimeErasePrompt()
    {
        using var directory = new TemporaryDirectory();
        var disk = UsbDisk("usb-001", 3);
        var controller = new UsbCreationController(inventoryProvider: new FakeInventory([disk]));

        var result = controller.Prepare(disk, CreateSetupMedia(directory.Path), CreatePackage(directory.Path));

        Assert.True(result.IsReadyForPhrase);
        Assert.Equal("ERASE", result.Confirmation!.Prompt.RequiredPhrase);
        Assert.Equal(disk.Identity.SerialNumber, result.Confirmation.Prompt.SerialOrDeviceId);
    }

    [Fact]
    public void Write_ExactPhraseAndFreshInventory_UsesResolvedTargetAndReportsReady()
    {
        using var directory = new TemporaryDirectory();
        var disk = UsbDisk("usb-001", 3);
        var target = new MemoryTarget();
        var controller = new UsbCreationController(
            inventoryProvider: new FakeInventory([disk]),
            volumeRootResolver: new FakeRootResolver("X:\\"),
            targetFactory: _ => target);
        var preparation = controller.Prepare(disk, CreateSetupMedia(directory.Path), CreatePackage(directory.Path));

        var result = controller.Write(preparation.Plan!, preparation.Confirmation!, "ERASE");

        Assert.True(result.IsReady);
        Assert.True(target.Session!.Committed);
        Assert.Contains("setup.exe", target.Session.Files.Keys);
        Assert.Contains("autounattend.xml", target.Session.Files.Keys);
    }

    private static string CreateSetupMedia(string root)
    {
        var setup = Path.Combine(root, "setup");
        WriteFile(setup, "setup.exe", "setup");
        return setup;
    }

    private static string CreatePackage(string root)
    {
        var package = Path.Combine(root, "package");
        Directory.CreateDirectory(package);
        var answerFile = Path.Combine(package, "autounattend.xml");
        File.WriteAllText(answerFile, "answer");
        var manifest = new DeploymentPackageManifest(
            1,
            Guid.NewGuid(),
            1,
            new WindowsDeploymentTarget(WindowsEdition.Professional, WindowsArchitecture.Amd64, "24H2", 26100),
            DeploymentCompatibilityState.Documented,
            [new DeploymentPackageManifestEntry("autounattend.xml", new FileInfo(answerFile).Length, Hash(answerFile))]);
        File.WriteAllBytes(Path.Combine(package, "deployment-manifest.json"), JsonSerializer.SerializeToUtf8Bytes(manifest, ManifestOptions));
        return package;
    }

    private static void WriteFile(string root, string relativePath, string contents)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

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

    private sealed class FakeInventory(IReadOnlyList<DiskInventoryItem> disks) : IRemovableDiskInventoryProvider
    {
        public DiskInventoryResult Read() => new(disks, []);
    }

    private sealed class FakeRootResolver(string root) : IUsbVolumeRootResolver
    {
        public UsbVolumeRootResolutionResult Resolve(DiskInventoryItem disk) => new(root, []);
    }

    private sealed class MemoryTarget : IUsbMediaWriteTarget
    {
        public MemorySession? Session { get; private set; }

        public IUsbMediaWriteSession Open(DiskInventoryItem authorizedDisk, UsbMediaWritePlan plan) => Session = new MemorySession();
    }

    private sealed class MemorySession : IUsbMediaWriteSession
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool Committed { get; private set; }

        public void Write(UsbMediaWriteFile file, Stream contents)
        {
            using var output = new MemoryStream();
            contents.CopyTo(output);
            Files[file.DestinationRelativePath] = output.ToArray();
        }

        public void Commit() => Committed = true;

        public UsbMediaWriteFileVerification ReadFinal(string destinationRelativePath)
        {
            var contents = Files[destinationRelativePath];
            return new UsbMediaWriteFileVerification(contents.Length, Convert.ToHexString(SHA256.HashData(contents)));
        }

        public void Abort()
        {
        }

        public void Dispose()
        {
        }
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
