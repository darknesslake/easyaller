using Easyaller.Core.Profiles;
using Easyaller.Deployment;
using System.Reflection;

namespace Easyaller.Core.Tests;

public sealed class WindowsIsoInspectionTests
{
    [Fact]
    public void Inspect_ValidAmd64ProfessionalIso_ReturnsHashAndReadyResult()
    {
        using var directory = new TemporaryDirectory();
        var isoPath = Path.Combine(directory.Path, "windows.iso");
        File.WriteAllBytes(isoPath, "abc"u8.ToArray());
        var service = new WindowsIsoInspectionService(new FakeContentReader(ValidReport()));

        var result = service.Inspect(new IsoInspectionRequest(isoPath, Target()));

        Assert.True(result.IsReadyForFutureUsbCreation);
        Assert.Equal("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", result.Sha256);
        Assert.Equal(3, result.SizeBytes);
    }

    [Fact]
    public void Inspect_TooLargeFile_DoesNotMountOrReadContent()
    {
        using var directory = new TemporaryDirectory();
        var isoPath = Path.Combine(directory.Path, "windows.iso");
        File.WriteAllBytes(isoPath, "abc"u8.ToArray());
        var reader = new FakeContentReader(ValidReport());
        var service = new WindowsIsoInspectionService(reader);

        var result = service.Inspect(new IsoInspectionRequest(isoPath, Target(), MaximumSizeBytes: 2));

        Assert.False(result.IsReadyForFutureUsbCreation);
        Assert.False(reader.WasRead);
        Assert.Contains(result.Errors, error => error.Code == "usb.iso.size.outOfRange");
    }

    [Fact]
    public void Inspect_MissingOrWrongEditionAndArchitecture_IsBlocked()
    {
        using var directory = new TemporaryDirectory();
        var isoPath = Path.Combine(directory.Path, "windows.iso");
        File.WriteAllBytes(isoPath, "abc"u8.ToArray());
        var report = ValidReport() with
        {
            Images = [new IsoInstallImage(1, "Windows Home", "Core", "arm64", null)],
        };
        var service = new WindowsIsoInspectionService(new FakeContentReader(report));

        var result = service.Inspect(new IsoInspectionRequest(isoPath, Target()));

        Assert.False(result.IsReadyForFutureUsbCreation);
        Assert.Contains(result.Errors, error => error.Code == "usb.iso.image.architecture.unsupported");
        Assert.Contains(result.Errors, error => error.Code == "usb.iso.target.edition.missing");
    }

    [Fact]
    public void Inspect_MissingWindowsSetupStructure_IsBlocked()
    {
        using var directory = new TemporaryDirectory();
        var isoPath = Path.Combine(directory.Path, "windows.iso");
        File.WriteAllBytes(isoPath, "abc"u8.ToArray());
        var report = ValidReport() with { RelativePaths = ["setup.exe", "sources/install.wim"] };
        var service = new WindowsIsoInspectionService(new FakeContentReader(report));

        var result = service.Inspect(new IsoInspectionRequest(isoPath, Target()));

        Assert.False(result.IsReadyForFutureUsbCreation);
        Assert.Equal(2, result.Errors.Count(error => error.Code == "usb.iso.structure.missing"));
    }

    [Fact]
    public void WindowsReader_UsesReadOnlyMountAndGuaranteedDismount()
    {
        var script = (string?)typeof(WindowsIsoContentReader)
            .GetField("ProbeScript", BindingFlags.Static | BindingFlags.NonPublic)
            ?.GetRawConstantValue();

        Assert.NotNull(script);
        Assert.Contains("Mount-DiskImage -ImagePath $isoPath -Access ReadOnly", script!, StringComparison.Ordinal);
        Assert.Contains("finally", script, StringComparison.Ordinal);
        Assert.Contains("Dismount-DiskImage -ImagePath $isoPath", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Format-Volume", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Clear-Disk", script, StringComparison.Ordinal);
    }

    private static WindowsDeploymentTarget Target() =>
        new(WindowsEdition.Professional, WindowsArchitecture.Amd64, "24H2", 26100);

    private static IsoContentReport ValidReport() => new(
        ["setup.exe", "sources/setup.exe", "sources/boot.wim", "sources/install.wim"],
        IsoPayloadFormat.Wim,
        [new IsoInstallImage(6, "Windows 11 Pro", "Professional", "amd64", "10.0.26100")]);

    private sealed class FakeContentReader(IsoContentReport report) : IIsoContentReader
    {
        public bool WasRead { get; private set; }

        public IsoContentReadResult Read(string isoPath)
        {
            WasRead = true;
            return new IsoContentReadResult(report, []);
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

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
