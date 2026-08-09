using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Easyaller.Core.Profiles;
using Easyaller.Deployment;

namespace Easyaller.Core.Tests;

public sealed class DeploymentPackageExporterTests
{
    private static readonly JsonSerializerOptions ManifestSerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    [Fact]
    public async Task ExportAsync_WritesVerifiedPackageThroughAStagingDirectory()
    {
        using var directory = new TemporaryDirectory();
        var sourceDirectory = Path.Combine(directory.Path, "source");
        Directory.CreateDirectory(sourceDirectory);
        var payloadSource = WriteSourceFile(sourceDirectory, "payload.json", "{\"setting\":true}\n");
        var scriptSource = WriteSourceFile(sourceDirectory, "verify.ps1", "Write-Output 'verify'\n");
        var installerSource = WriteSourceFile(sourceDirectory, "ContosoSetup.exe", "installer bytes\n");
        var defaultProfile = ProvisioningProfileFactory.CreateDefault();
        var profile = defaultProfile with
        {
            Applications =
            [
                new ApplicationProfile(
                    "contoso-setup",
                    "Contoso Setup",
                    ApplicationSourceKind.PackageRelative,
                    "installers/ContosoSetup.exe",
                    []),
            ],
        };
        var dryRun = CreateDryRun(profile);
        var destinationDirectory = Path.Combine(directory.Path, "output", "package");
        var request = new DeploymentPackageExportRequest(
            dryRun,
            destinationDirectory,
            [
                new DeploymentPackageAsset(DeploymentPackageAssetKind.LocalPayload, payloadSource, "payload/payload.json"),
                new DeploymentPackageAsset(DeploymentPackageAssetKind.Script, scriptSource, "scripts/verify.ps1"),
                new DeploymentPackageAsset(DeploymentPackageAssetKind.Installer, installerSource, "installers/ContosoSetup.exe"),
            ]);

        var result = await new DeploymentPackageExporter().ExportAsync(request);

        Assert.True(result.IsSuccess);
        Assert.Equal(Path.GetFullPath(destinationDirectory), result.DestinationDirectory);
        Assert.NotNull(result.Manifest);
        Assert.True(File.Exists(Path.Combine(destinationDirectory, "autounattend.xml")));
        Assert.Equal(dryRun.AnswerFile.ToArray(), File.ReadAllBytes(Path.Combine(destinationDirectory, "autounattend.xml")));
        Assert.Equal("{\"setting\":true}\n", File.ReadAllText(Path.Combine(destinationDirectory, "payload", "payload.json")));
        Assert.Equal("Write-Output 'verify'\n", File.ReadAllText(Path.Combine(destinationDirectory, "scripts", "verify.ps1")));
        Assert.Equal("installer bytes\n", File.ReadAllText(Path.Combine(destinationDirectory, "installers", "ContosoSetup.exe")));
        Assert.True(new ProfileJsonSerializer().Read(File.ReadAllBytes(Path.Combine(destinationDirectory, "selected-profile.wpprofile.json"))).IsValid);

        var manifest = JsonSerializer.Deserialize<DeploymentPackageManifest>(
            File.ReadAllBytes(Path.Combine(destinationDirectory, "deployment-manifest.json")),
            ManifestSerializerOptions);
        Assert.NotNull(manifest);
        Assert.Equal(profile.ProfileId, manifest.ProfileId);
        Assert.Equal(6, manifest.Files.Count);
        Assert.All(manifest.Files, entry =>
        {
            var filePath = Path.Combine(destinationDirectory, entry.RelativePath);
            Assert.Equal(entry.Length, new FileInfo(filePath).Length);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(filePath))), entry.Sha256);
        });
        Assert.DoesNotContain(manifest.Files, entry => entry.RelativePath == "deployment-manifest.json");
        Assert.DoesNotContain(Directory.GetDirectories(Path.Combine(directory.Path, "output")), path =>
            Path.GetFileName(path).StartsWith(".easyaller-staging-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExportAsync_ExistingDestination_IsBlockedWithoutReplacingFiles()
    {
        using var directory = new TemporaryDirectory();
        var destinationDirectory = Path.Combine(directory.Path, "existing-package");
        Directory.CreateDirectory(destinationDirectory);
        var sentinelPath = Path.Combine(destinationDirectory, "keep.txt");
        File.WriteAllText(sentinelPath, "keep me");

        var result = await new DeploymentPackageExporter().ExportAsync(new DeploymentPackageExportRequest(
            CreateDryRun(ProvisioningProfileFactory.CreateDefault()),
            destinationDirectory,
            []));

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, error => error.Code == "deployment.package.export.destination.exists");
        Assert.Equal("keep me", File.ReadAllText(sentinelPath));
        Assert.False(File.Exists(Path.Combine(destinationDirectory, "autounattend.xml")));
    }

    [Fact]
    public async Task ExportAsync_UndeclaredInstallerOrUnsafeAssetPath_IsBlockedBeforeCreatingDestination()
    {
        using var directory = new TemporaryDirectory();
        var sourcePath = WriteSourceFile(directory.Path, "setup.exe", "installer bytes");
        var destinationDirectory = Path.Combine(directory.Path, "blocked-package");
        var request = new DeploymentPackageExportRequest(
            CreateDryRun(ProvisioningProfileFactory.CreateDefault()),
            destinationDirectory,
            [
                new DeploymentPackageAsset(DeploymentPackageAssetKind.Installer, sourcePath, "installers/setup.exe"),
                new DeploymentPackageAsset(DeploymentPackageAssetKind.Script, sourcePath, "scripts/../unsafe.ps1"),
            ]);

        var result = await new DeploymentPackageExporter().ExportAsync(request);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, error => error.Code == "deployment.package.asset.installer.notAllowed");
        Assert.Contains(result.Errors, error => error.Code == "deployment.package.asset.path.unsafe");
        Assert.False(Directory.Exists(destinationDirectory));
    }

    private static DeploymentDryRun CreateDryRun(ProvisioningProfile profile)
    {
        var request = new DeploymentPreparationRequest(
            profile,
            new WindowsDeploymentTarget(WindowsEdition.Professional, WindowsArchitecture.Amd64, "24H2", 26100));
        var result = new DeploymentDryRunService().CreateDryRun(request);
        return result.DryRun!;
    }

    private static string WriteSourceFile(string directory, string fileName, string content)
    {
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Easyaller.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
