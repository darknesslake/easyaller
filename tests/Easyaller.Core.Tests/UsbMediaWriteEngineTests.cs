using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Easyaller.Core.Profiles;
using Easyaller.Deployment;

namespace Easyaller.Core.Tests;

public sealed class UsbMediaWriteEngineTests
{
    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    [Fact]
    public void CreatePlan_ValidSetupAndVerifiedPackage_ProducesDistinctHashedFiles()
    {
        using var directory = new TemporaryDirectory();
        var setup = CreateSetupMedia(directory.Path);
        var package = CreatePackage(directory.Path);
        var disk = UsbDisk("usb-001", 3);

        var result = new UsbMediaWriteEngine().CreatePlan(new UsbMediaWritePlanRequest(
            new RemovableDiskSafetyService().Select(disk),
            setup,
            package));

        Assert.True(result.IsReadyForAuthorizedWrite);
        Assert.Equal(4, result.Plan!.Files.Count);
        Assert.Contains(result.Plan.Files, file => file.DestinationRelativePath == "setup.exe" && file.Source == UsbMediaWriteFileSource.WindowsSetup);
        Assert.Contains(result.Plan.Files, file => file.DestinationRelativePath == "autounattend.xml" && file.Source == UsbMediaWriteFileSource.DeploymentPackage);
        Assert.All(result.Plan.Files, file => Assert.Equal(64, file.Sha256.Length));
    }

    [Fact]
    public void Execute_AuthorizedDiskAndMatchingOutput_ReportsReadyOnlyAfterEveryHashMatches()
    {
        using var directory = new TemporaryDirectory();
        var setup = CreateSetupMedia(directory.Path);
        var package = CreatePackage(directory.Path);
        var disk = UsbDisk("usb-001", 3);
        var engine = new UsbMediaWriteEngine();
        var plan = engine.CreatePlan(new UsbMediaWritePlanRequest(
            new RemovableDiskSafetyService().Select(disk), setup, package)).Plan!;
        var target = new MemoryTarget();

        var result = engine.Execute(plan, Authorize(disk), target);

        Assert.True(result.IsReady);
        Assert.Equal(plan.Files.Count, result.VerifiedFileCount);
        Assert.True(target.Session!.Committed);
        Assert.Equal("setup", target.Session.Files["setup.exe"]);
        Assert.Equal("answer", target.Session.Files["autounattend.xml"]);
    }

    [Fact]
    public void Execute_SourceChangedAfterPlanning_BlocksBeforeAnyFileIsCopied()
    {
        using var directory = new TemporaryDirectory();
        var setup = CreateSetupMedia(directory.Path);
        var package = CreatePackage(directory.Path);
        var disk = UsbDisk("usb-001", 3);
        var engine = new UsbMediaWriteEngine();
        var plan = engine.CreatePlan(new UsbMediaWritePlanRequest(
            new RemovableDiskSafetyService().Select(disk), setup, package)).Plan!;
        File.WriteAllText(Path.Combine(setup, "setup.exe"), "changed");
        var target = new MemoryTarget();

        var result = engine.Execute(plan, Authorize(disk), target);

        Assert.False(result.IsReady);
        Assert.Contains(result.Errors, error => error.Code == "usb.write.source.changed");
        Assert.Null(target.Session);
    }

    [Fact]
    public void Execute_MismatchingFinalHash_NeverReportsReady()
    {
        using var directory = new TemporaryDirectory();
        var setup = CreateSetupMedia(directory.Path);
        var package = CreatePackage(directory.Path);
        var disk = UsbDisk("usb-001", 3);
        var engine = new UsbMediaWriteEngine();
        var plan = engine.CreatePlan(new UsbMediaWritePlanRequest(
            new RemovableDiskSafetyService().Select(disk), setup, package)).Plan!;
        var target = new MemoryTarget(corruptFinal: true);

        var result = engine.Execute(plan, Authorize(disk), target);

        Assert.False(result.IsReady);
        Assert.Equal(0, result.VerifiedFileCount);
        Assert.Contains(result.Errors, error => error.Code == "usb.write.output.hash.mismatch");
        Assert.True(target.Session!.Committed);
    }

    private static string CreateSetupMedia(string root)
    {
        var setup = Path.Combine(root, "setup-media");
        WriteFile(setup, "setup.exe", "setup");
        WriteFile(setup, "sources/setup.exe", "setup source");
        return setup;
    }

    private static string CreatePackage(string root)
    {
        var package = Path.Combine(root, "package");
        Directory.CreateDirectory(package);
        WriteFile(package, "autounattend.xml", "answer");
        var answerPath = Path.Combine(package, "autounattend.xml");
        var manifest = new DeploymentPackageManifest(
            1,
            Guid.NewGuid(),
            1,
            new WindowsDeploymentTarget(WindowsEdition.Professional, WindowsArchitecture.Amd64, "24H2", 26100),
            DeploymentCompatibilityState.Documented,
            [new DeploymentPackageManifestEntry(
                "autounattend.xml",
                new FileInfo(answerPath).Length,
                Hash(answerPath))]);
        File.WriteAllBytes(Path.Combine(package, "deployment-manifest.json"), JsonSerializer.SerializeToUtf8Bytes(manifest, ManifestOptions));
        return package;
    }

    private static string WriteFile(string root, string relativePath, string contents)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return Path.GetDirectoryName(path)!;
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static UsbDestructiveConfirmationResult Authorize(DiskInventoryItem disk)
    {
        var safety = new RemovableDiskSafetyService();
        var confirmationMachine = new UsbDestructiveConfirmationStateMachine(safety);
        var confirmation = confirmationMachine.Begin(safety.Select(disk)).Confirmation!;
        confirmationMachine.Submit(confirmation, "ERASE");
        return confirmationMachine.AuthorizeFirstWrite(confirmation, [disk]);
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

    private sealed class MemoryTarget(bool corruptFinal = false) : IUsbMediaWriteTarget
    {
        private readonly bool _corruptFinal = corruptFinal;

        public MemorySession? Session { get; private set; }

        public IUsbMediaWriteSession Open(DiskInventoryItem authorizedDisk, UsbMediaWritePlan plan) =>
            Session = new MemorySession(_corruptFinal);
    }

    private sealed class MemorySession(bool corruptFinal) : IUsbMediaWriteSession
    {
        public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool Committed { get; private set; }

        public bool Aborted { get; private set; }

        public void Write(UsbMediaWriteFile file, Stream contents)
        {
            using var reader = new StreamReader(contents, leaveOpen: true);
            Files[file.DestinationRelativePath] = reader.ReadToEnd();
        }

        public void Commit() => Committed = true;

        public UsbMediaWriteFileVerification ReadFinal(string destinationRelativePath)
        {
            var contents = corruptFinal && destinationRelativePath == "setup.exe"
                ? "corrupted"
                : Files[destinationRelativePath];
            var bytes = System.Text.Encoding.UTF8.GetBytes(contents);
            return new UsbMediaWriteFileVerification(bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)));
        }

        public void Abort() => Aborted = true;

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
