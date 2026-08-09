using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Easyaller.Deployment;

public enum UsbMediaWriteFileSource
{
    WindowsSetup,
    DeploymentPackage,
}

public sealed record UsbMediaWriteFile(
    UsbMediaWriteFileSource Source,
    string SourcePath,
    string DestinationRelativePath,
    long Length,
    string Sha256);

public sealed record UsbMediaWritePlan(
    DiskSelection Selection,
    IReadOnlyList<UsbMediaWriteFile> Files);

public sealed record UsbMediaWritePlanRequest(
    DiskSelectionVerification Selection,
    string SetupMediaDirectory,
    string DeploymentPackageDirectory);

public sealed record UsbMediaWritePlanResult(
    UsbMediaWritePlan? Plan,
    IReadOnlyList<DeploymentValidationError> Errors)
{
    public bool IsReadyForAuthorizedWrite => Plan is not null && Errors.Count == 0;
}

public sealed record UsbMediaWriteFileVerification(long Length, string Sha256)
{
    public bool Matches(UsbMediaWriteFile file) =>
        Length == file.Length && string.Equals(Sha256, file.Sha256, StringComparison.Ordinal);
}

public interface IUsbMediaWriteTarget
{
    IUsbMediaWriteSession Open(DiskInventoryItem authorizedDisk, UsbMediaWritePlan plan);
}

public interface IUsbMediaWriteSession : IDisposable
{
    void Write(UsbMediaWriteFile file, Stream contents);

    void Commit();

    UsbMediaWriteFileVerification ReadFinal(string destinationRelativePath);

    void Abort();
}

public sealed record UsbMediaWriteExecutionResult(
    bool IsReady,
    int VerifiedFileCount,
    IReadOnlyList<DeploymentValidationError> Errors);

public sealed class UsbMediaWriteEngine
{
    private const string PackageManifestPath = "deployment-manifest.json";

    private static readonly JsonSerializerOptions ManifestSerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    public UsbMediaWritePlanResult CreatePlan(UsbMediaWritePlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new List<DeploymentValidationError>();
        if (!request.Selection.IsStillEligible)
        {
            errors.AddRange(request.Selection.Errors.Count > 0
                ? request.Selection.Errors
                : [Error("usb.write.selection.ineligible", "selection", "Only an eligible explicitly selected removable disk can receive a write plan.")]);
            return new UsbMediaWritePlanResult(null, errors);
        }

        var setupRoot = GetExistingAbsoluteDirectory(request.SetupMediaDirectory, "setupMediaDirectory", errors);
        var packageRoot = GetExistingAbsoluteDirectory(request.DeploymentPackageDirectory, "deploymentPackageDirectory", errors);
        if (errors.Count > 0 || setupRoot is null || packageRoot is null)
        {
            return new UsbMediaWritePlanResult(null, errors);
        }

        var files = new List<UsbMediaWriteFile>();
        AddDirectoryFiles(UsbMediaWriteFileSource.WindowsSetup, setupRoot, files, errors);
        AddDeploymentPackageFiles(packageRoot, files, errors);
        ValidateDestinationPaths(files, errors);
        if (errors.Count > 0)
        {
            return new UsbMediaWritePlanResult(null, errors);
        }

        return new UsbMediaWritePlanResult(
            new UsbMediaWritePlan(
                request.Selection.Selection,
                files.OrderBy(static file => file.DestinationRelativePath, StringComparer.Ordinal).ToArray()),
            []);
    }

    public UsbMediaWriteExecutionResult Execute(
        UsbMediaWritePlan plan,
        UsbDestructiveConfirmationResult authorization,
        IUsbMediaWriteTarget target)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(target);

        if (!authorization.IsAuthorizedForFirstWrite || authorization.CurrentDisk is null)
        {
            return Failure("usb.write.authorization.missing", "authorization", "A current one-time disk authorization is required before copying files.");
        }

        if (!MatchesSelection(plan.Selection, authorization.CurrentDisk))
        {
            return Failure("usb.write.authorization.disk.mismatch", "authorization", "Authorized disk identity does not match the planned disk.");
        }

        foreach (var file in plan.Files)
        {
            if (!CurrentSourceMatches(file))
            {
                return Failure("usb.write.source.changed", "plan.files", "A planned source file changed before copying could begin.");
            }
        }

        IUsbMediaWriteSession? session = null;
        var committed = false;
        try
        {
            session = target.Open(authorization.CurrentDisk, plan);
            foreach (var file in plan.Files)
            {
                using var source = new FileStream(file.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                session.Write(file, source);
            }

            session.Commit();
            committed = true;
            foreach (var file in plan.Files)
            {
                if (!session.ReadFinal(file.DestinationRelativePath).Matches(file))
                {
                    return Failure("usb.write.output.hash.mismatch", "plan.files", "A copied output file did not match its planned SHA-256 hash.");
                }
            }

            return new UsbMediaWriteExecutionResult(true, plan.Files.Count, []);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Failure("usb.write.failed", "target", "USB media write did not complete: " + exception.Message);
        }
        finally
        {
            if (session is not null)
            {
                if (!committed)
                {
                    session.Abort();
                }

                session.Dispose();
            }
        }
    }

    private static string? GetExistingAbsoluteDirectory(
        string path,
        string fieldPath,
        ICollection<DeploymentValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !Directory.Exists(path))
        {
            errors.Add(Error("usb.write.directory.invalid", fieldPath, "Directory must be an existing absolute local path."));
            return null;
        }

        return Path.GetFullPath(path);
    }

    private static void AddDirectoryFiles(
        UsbMediaWriteFileSource source,
        string rootDirectory,
        ICollection<UsbMediaWriteFile> files,
        ICollection<DeploymentValidationError> errors)
    {
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(rootDirectory, "*", SearchOption.AllDirectories))
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                {
                    errors.Add(Error("usb.write.source.reparsePoint", "source", "Source directory must not contain symbolic links or other reparse points."));
                }
            }

            foreach (var path in Directory.EnumerateFiles(rootDirectory, "*", SearchOption.AllDirectories))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                {
                    errors.Add(Error("usb.write.source.reparsePoint", "source", "Source file must not be a symbolic link or other reparse point."));
                    continue;
                }

                var relativePath = NormalizeRelativePath(Path.GetRelativePath(rootDirectory, path));
                if (!IsSafeRelativePath(relativePath))
                {
                    errors.Add(Error("usb.write.source.path.unsafe", "source", "Source file path escaped its source root."));
                    continue;
                }

                files.Add(CreateWriteFile(source, path, relativePath));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            errors.Add(Error("usb.write.source.read.failed", "source", exception.Message));
        }
    }

    private static void AddDeploymentPackageFiles(
        string packageRoot,
        ICollection<UsbMediaWriteFile> files,
        ICollection<DeploymentValidationError> errors)
    {
        var manifestPath = Path.Combine(packageRoot, PackageManifestPath);
        DeploymentPackageManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<DeploymentPackageManifest>(File.ReadAllBytes(manifestPath), ManifestSerializerOptions);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            errors.Add(Error("usb.write.package.manifest.invalid", "deploymentPackageDirectory", "Deployment package manifest cannot be read: " + exception.Message));
            return;
        }

        if (manifest is null || manifest.FormatVersion != 1 || manifest.Files.Count == 0)
        {
            errors.Add(Error("usb.write.package.manifest.invalid", "deploymentPackageDirectory", "Deployment package manifest is empty or unsupported."));
            return;
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Files)
        {
            var relativePath = NormalizeRelativePath(entry.RelativePath);
            if (!IsSafeRelativePath(relativePath) || !paths.Add(relativePath) || entry.Length < 0 || !IsSha256(entry.Sha256))
            {
                errors.Add(Error("usb.write.package.manifest.entry.invalid", "deploymentPackageDirectory", "Deployment package manifest contains an invalid file entry."));
                continue;
            }

            var sourcePath = GetContainedPath(packageRoot, relativePath);
            if (!File.Exists(sourcePath))
            {
                errors.Add(Error("usb.write.package.file.missing", "deploymentPackageDirectory", "Deployment package file is missing."));
                continue;
            }

            var actual = CreateWriteFile(UsbMediaWriteFileSource.DeploymentPackage, sourcePath, relativePath);
            if (actual.Length != entry.Length || !string.Equals(actual.Sha256, entry.Sha256, StringComparison.Ordinal))
            {
                errors.Add(Error("usb.write.package.hash.mismatch", "deploymentPackageDirectory", "Deployment package file does not match its manifest hash."));
                continue;
            }

            files.Add(actual);
        }

        if (!paths.Contains("autounattend.xml"))
        {
            errors.Add(Error("usb.write.package.answerFile.missing", "deploymentPackageDirectory", "Deployment package must contain autounattend.xml."));
        }

        if (File.Exists(manifestPath))
        {
            files.Add(CreateWriteFile(UsbMediaWriteFileSource.DeploymentPackage, manifestPath, PackageManifestPath));
        }
    }

    private static void ValidateDestinationPaths(IReadOnlyCollection<UsbMediaWriteFile> files, ICollection<DeploymentValidationError> errors)
    {
        if (files.Count == 0)
        {
            errors.Add(Error("usb.write.plan.empty", "plan.files", "USB media plan must contain setup and deployment files."));
            return;
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (!paths.Add(file.DestinationRelativePath))
            {
                errors.Add(Error("usb.write.destination.duplicate", "plan.files", "Windows Setup and deployment package files cannot overwrite one another."));
            }
        }
    }

    private static UsbMediaWriteFile CreateWriteFile(UsbMediaWriteFileSource source, string path, string relativePath)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var verification = CreateVerification(stream);
        return new UsbMediaWriteFile(source, Path.GetFullPath(path), NormalizeRelativePath(relativePath), verification.Length, verification.Sha256);
    }

    private static bool CurrentSourceMatches(UsbMediaWriteFile file)
    {
        try
        {
            using var stream = new FileStream(file.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return CreateVerification(stream).Matches(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static UsbMediaWriteFileVerification CreateVerification(Stream contents) =>
        new(contents.Length, Convert.ToHexString(SHA256.HashData(contents)));

    private static bool MatchesSelection(DiskSelection selection, DiskInventoryItem disk) =>
        string.Equals(selection.Identity.UniqueId, disk.Identity.UniqueId, StringComparison.Ordinal) &&
        (string.IsNullOrWhiteSpace(selection.Identity.SerialNumber) ||
         string.Equals(selection.Identity.SerialNumber, disk.Identity.SerialNumber, StringComparison.Ordinal));

    private static bool IsSafeRelativePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        !Path.IsPathRooted(path) &&
        !path.Split('/').Any(static segment => segment is "" or "." or "..");

    private static bool IsSha256(string? hash) =>
        !string.IsNullOrWhiteSpace(hash) && hash.Length == 64 && hash.All(Uri.IsHexDigit);

    private static string NormalizeRelativePath(string path) => path.Replace('\\', '/');

    private static string GetContainedPath(string rootDirectory, string relativePath)
    {
        var root = Path.GetFullPath(rootDirectory);
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(rootWithSeparator, StringComparison.Ordinal)
            ? path
            : throw new InvalidOperationException("Package file path escaped its root directory.");
    }

    private static UsbMediaWriteExecutionResult Failure(string code, string fieldPath, string message) =>
        new(false, 0, [Error(code, fieldPath, message)]);

    private static DeploymentValidationError Error(string code, string fieldPath, string message) => new(code, fieldPath, message);
}
