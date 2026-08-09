using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Easyaller.Core.Profiles;

namespace Easyaller.Deployment;

public sealed class DeploymentPackageExporter : IDeploymentPackageExporter
{
    private const string AnswerFilePath = "autounattend.xml";
    private const string ProfilePath = "selected-profile.wpprofile.json";
    private const string ManifestPath = "deployment-manifest.json";
    private const string InstructionsPath = "README.txt";
    private const int ManifestFormatVersion = 1;

    private static readonly JsonSerializerOptions ManifestSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    private readonly ProfileJsonSerializer _profileSerializer;

    public DeploymentPackageExporter(ProfileJsonSerializer? profileSerializer = null)
    {
        _profileSerializer = profileSerializer ?? new ProfileJsonSerializer();
    }

    public Task<DeploymentPackageExportResult> ExportAsync(
        DeploymentPackageExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.DryRun);
        ArgumentNullException.ThrowIfNull(request.Assets);

        var validationErrors = ValidateRequest(request);
        if (validationErrors.Count > 0)
        {
            return Task.FromResult(Failure(validationErrors));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(Failure(new DeploymentValidationError(
                "deployment.package.export.cancelled",
                "destinationDirectory",
                "Deployment package export was cancelled.")));
        }

        var destinationDirectory = Path.GetFullPath(request.DestinationDirectory);
        var parentDirectory = Path.GetDirectoryName(destinationDirectory)!;
        var stagingDirectory = Path.Combine(parentDirectory, ".easyaller-staging-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(parentDirectory);
            Directory.CreateDirectory(stagingDirectory);

            var entries = new List<DeploymentPackageManifestEntry>();
            WriteFile(stagingDirectory, AnswerFilePath, request.DryRun.AnswerFile.Span, entries);
            WriteFile(stagingDirectory, ProfilePath, _profileSerializer.Serialize(request.DryRun.EffectiveProfile), entries);
            WriteFile(stagingDirectory, InstructionsPath, CreateInstructions(), entries);

            foreach (var asset in request.Assets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CopyFile(stagingDirectory, asset.SourceFilePath, asset.RelativePath, entries);
            }

            WritePayloadManifest(stagingDirectory, request.DryRun, entries);
            VerifyEntries(stagingDirectory, entries);
            var manifest = new DeploymentPackageManifest(
                ManifestFormatVersion,
                request.DryRun.EffectiveProfile.ProfileId,
                request.DryRun.EffectiveProfile.Revision,
                request.DryRun.Preview.Target,
                request.DryRun.Preview.CompatibilityState,
                entries.OrderBy(static entry => entry.RelativePath, StringComparer.Ordinal).ToArray());
            File.WriteAllBytes(
                Path.Combine(stagingDirectory, ManifestPath),
                JsonSerializer.SerializeToUtf8Bytes(manifest, ManifestSerializerOptions));

            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(stagingDirectory, destinationDirectory);
            return Task.FromResult(new DeploymentPackageExportResult(true, destinationDirectory, manifest, []));
        }
        catch (OperationCanceledException)
        {
            return Task.FromResult(Failure(new DeploymentValidationError(
                "deployment.package.export.cancelled",
                "destinationDirectory",
                "Deployment package export was cancelled.")));
        }
        catch (IOException)
        {
            return Task.FromResult(Failure(new DeploymentValidationError(
                "deployment.package.export.io",
                "destinationDirectory",
                "Deployment package export was not completed.")));
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(Failure(new DeploymentValidationError(
                "deployment.package.export.io",
                "destinationDirectory",
                "Deployment package export was not completed.")));
        }
        finally
        {
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
        }
    }

    private static IReadOnlyList<DeploymentValidationError> ValidateRequest(DeploymentPackageExportRequest request)
    {
        var errors = new List<DeploymentValidationError>();
        if (!request.DryRun.IsFileOnly || !request.DryRun.Preview.IsFileOnly)
        {
            errors.Add(new DeploymentValidationError(
                "deployment.package.export.nonFileOnly",
                "dryRun",
                "Deployment package export supports file-only dry runs only."));
        }

        if (string.IsNullOrWhiteSpace(request.DestinationDirectory))
        {
            errors.Add(new DeploymentValidationError(
                "deployment.package.export.destination.required",
                "destinationDirectory",
                "Deployment package destination is required."));
            return errors;
        }

        var destinationDirectory = Path.GetFullPath(request.DestinationDirectory);
        if (Directory.Exists(destinationDirectory) || File.Exists(destinationDirectory))
        {
            errors.Add(new DeploymentValidationError(
                "deployment.package.export.destination.exists",
                "destinationDirectory",
                "Deployment package destination must not already exist."));
        }

        var reservedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            AnswerFilePath,
            ProfilePath,
            ManifestPath,
            InstructionsPath,
        };
        foreach (var asset in request.Assets)
        {
            ValidateAsset(asset, request.DryRun.EffectiveProfile, reservedPaths, errors);
        }

        return errors;
    }

    private static void ValidateAsset(
        DeploymentPackageAsset asset,
        ProvisioningProfile profile,
        ISet<string> reservedPaths,
        ICollection<DeploymentValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(asset.SourceFilePath) || !File.Exists(asset.SourceFilePath))
        {
            errors.Add(new DeploymentValidationError(
                "deployment.package.asset.source.missing",
                "assets.sourceFilePath",
                "Package asset source file does not exist."));
            return;
        }

        if ((File.GetAttributes(asset.SourceFilePath) & FileAttributes.ReparsePoint) != 0)
        {
            errors.Add(new DeploymentValidationError(
                "deployment.package.asset.source.symlink",
                "assets.sourceFilePath",
                "Package asset source file must not be a symbolic link."));
        }

        if (!IsSafeRelativePath(asset.RelativePath) || !HasRequiredDirectory(asset.Kind, asset.RelativePath))
        {
            errors.Add(new DeploymentValidationError(
                "deployment.package.asset.path.unsafe",
                "assets.relativePath",
                "Package asset path must stay in its allowed package directory."));
            return;
        }

        var normalizedRelativePath = NormalizeRelativePath(asset.RelativePath);
        if (!reservedPaths.Add(normalizedRelativePath))
        {
            errors.Add(new DeploymentValidationError(
                "deployment.package.asset.path.duplicate",
                "assets.relativePath",
                "Package asset path conflicts with another package file."));
        }

        if (asset.Kind == DeploymentPackageAssetKind.Installer && !profile.Applications.Any(application =>
                application.SourceKind == ApplicationSourceKind.PackageRelative &&
                string.Equals(
                    ConfigurationSetPayloadLayout.RootRelativePath + "/" + NormalizeRelativePath(application.PackageRelativePath!),
                    normalizedRelativePath,
                    StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add(new DeploymentValidationError(
                "deployment.package.asset.installer.notAllowed",
                "assets.relativePath",
                "Installer asset is not declared as a package-relative application in the selected profile."));
        }
    }

    private static bool IsSafeRelativePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        !Path.IsPathRooted(path) &&
        !path.Split(['/', '\\']).Any(static segment => segment is "" or "." or "..");

    private static bool HasRequiredDirectory(DeploymentPackageAssetKind kind, string relativePath)
    {
        var normalizedPath = NormalizeRelativePath(relativePath);
        return normalizedPath.StartsWith(ConfigurationSetPayloadLayout.GetAssetDirectory(kind), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRelativePath(string path) => path.Replace('\\', '/');

    private static void WriteFile(
        string stagingDirectory,
        string relativePath,
        ReadOnlySpan<byte> contents,
        ICollection<DeploymentPackageManifestEntry> entries)
    {
        var path = Path.Combine(stagingDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, contents.ToArray());
        entries.Add(CreateManifestEntry(relativePath, path));
    }

    private static void CopyFile(
        string stagingDirectory,
        string sourceFilePath,
        string relativePath,
        ICollection<DeploymentPackageManifestEntry> entries)
    {
        var path = Path.Combine(stagingDirectory, NormalizeRelativePath(relativePath));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(sourceFilePath, path, overwrite: false);
        entries.Add(CreateManifestEntry(NormalizeRelativePath(relativePath), path));
    }

    private static DeploymentPackageManifestEntry CreateManifestEntry(string relativePath, string fullPath) => new(
        NormalizeRelativePath(relativePath),
        new FileInfo(fullPath).Length,
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fullPath))));

    private static void VerifyEntries(string stagingDirectory, IEnumerable<DeploymentPackageManifestEntry> entries)
    {
        foreach (var entry in entries)
        {
            var path = Path.Combine(stagingDirectory, entry.RelativePath);
            var current = CreateManifestEntry(entry.RelativePath, path);
            if (current.Length != entry.Length || !string.Equals(current.Sha256, entry.Sha256, StringComparison.Ordinal))
            {
                throw new IOException("Deployment package integrity verification failed.");
            }
        }
    }

    private static void WritePayloadManifest(
        string stagingDirectory,
        DeploymentDryRun dryRun,
        ICollection<DeploymentPackageManifestEntry> entries)
    {
        var payloadEntries = entries
            .Where(static entry => ConfigurationSetPayloadLayout.IsPayloadAssetPath(entry.RelativePath))
            .Select(static entry => entry with
            {
                RelativePath = ConfigurationSetPayloadLayout.GetPayloadRelativePath(entry.RelativePath),
            })
            .OrderBy(static entry => entry.RelativePath, StringComparer.Ordinal)
            .ToArray();
        if (payloadEntries.Length == 0)
        {
            return;
        }

        var manifest = new DeploymentPackageManifest(
            ManifestFormatVersion,
            dryRun.EffectiveProfile.ProfileId,
            dryRun.EffectiveProfile.Revision,
            dryRun.Preview.Target,
            dryRun.Preview.CompatibilityState,
            payloadEntries);
        WriteFile(
            stagingDirectory,
            ConfigurationSetPayloadLayout.PayloadManifestRelativePath,
            JsonSerializer.SerializeToUtf8Bytes(manifest, ManifestSerializerOptions),
            entries);
    }

    private static byte[] CreateInstructions() => Encoding.UTF8.GetBytes(
        "Easyaller deployment package\n" +
        "\n" +
        "This package is file-only. It does not format disks, mount images, create USB media, or change Windows by itself.\n" +
        "Review deployment-manifest.json and verify every listed SHA-256 hash before use.\n" +
        "Treat this package as sensitive. Do not commit it to Git or share it without review.\n");

    private static DeploymentPackageExportResult Failure(params DeploymentValidationError[] errors) =>
        new(false, null, null, errors);

    private static DeploymentPackageExportResult Failure(IReadOnlyList<DeploymentValidationError> errors) =>
        new(false, null, null, errors);
}
