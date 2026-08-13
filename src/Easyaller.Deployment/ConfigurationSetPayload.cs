using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Easyaller.Core;

namespace Easyaller.Deployment;

public static class ConfigurationSetPayloadLayout
{
    public const string RootRelativePath = "$OEM$/$1/ProgramData/Easyaller";
    public const string PayloadManifestFileName = "payload-manifest.json";
    public const string PayloadManifestRelativePath = RootRelativePath + "/" + PayloadManifestFileName;

    public static string GetAssetDirectory(DeploymentPackageAssetKind kind) => kind switch
    {
        DeploymentPackageAssetKind.LocalPayload => RootRelativePath + "/payload/",
        DeploymentPackageAssetKind.Script => RootRelativePath + "/scripts/",
        DeploymentPackageAssetKind.Installer => RootRelativePath + "/installers/",
        _ => string.Empty,
    };

    public static bool IsPayloadAssetPath(string relativePath) =>
        relativePath.StartsWith(RootRelativePath + "/", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(relativePath, PayloadManifestRelativePath, StringComparison.OrdinalIgnoreCase);

    public static string GetPayloadRelativePath(string packageRelativePath) =>
        packageRelativePath[(RootRelativePath.Length + 1)..];
}

public sealed class ConfigurationSetPayloadVerifier : IConfigurationSetPayloadVerifier
{
    private static readonly JsonSerializerOptions ManifestSerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    public PayloadVerificationResult Verify(string payloadDirectory)
    {
        if (string.IsNullOrWhiteSpace(payloadDirectory) || !Directory.Exists(payloadDirectory))
        {
            return Failure("deployment.payload.directory.missing", "payloadDirectory", "Configuration-set payload directory does not exist.");
        }

        var manifestPath = Path.Combine(payloadDirectory, ConfigurationSetPayloadLayout.PayloadManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return Failure("deployment.payload.manifest.missing", "payloadManifest", "Configuration-set payload manifest does not exist.");
        }

        DeploymentPackageManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<DeploymentPackageManifest>(File.ReadAllBytes(manifestPath), ManifestSerializerOptions);
        }
        catch (JsonException)
        {
            return Failure("deployment.payload.manifest.invalid", "payloadManifest", "Configuration-set payload manifest is invalid.");
        }
        catch (IOException)
        {
            return Failure("deployment.payload.manifest.unreadable", "payloadManifest", "Configuration-set payload manifest cannot be read.");
        }
        catch (UnauthorizedAccessException)
        {
            return Failure("deployment.payload.manifest.unreadable", "payloadManifest", "Configuration-set payload manifest cannot be read.");
        }

        if (manifest is null || manifest.FormatVersion != 1)
        {
            return Failure("deployment.payload.manifest.invalid", "payloadManifest", "Configuration-set payload manifest is invalid.");
        }

        var errors = ValidateEntries(manifest.Files);
        if (errors.Count > 0)
        {
            return new PayloadVerificationResult(false, 0, errors);
        }

        var verifiedFiles = 0;
        var rootDirectory = Path.GetFullPath(payloadDirectory);
        foreach (var entry in manifest.Files)
        {
            var filePath = GetContainedFilePath(rootDirectory, entry.RelativePath);
            if (!File.Exists(filePath))
            {
                return Failure("deployment.payload.file.missing", "payloadManifest.files", "A configuration-set payload file is missing.");
            }

            var info = new FileInfo(filePath);
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(filePath)));
            if (info.Length != entry.Length || !string.Equals(hash, entry.Sha256, StringComparison.Ordinal))
            {
                return Failure("deployment.payload.hash.mismatch", "payloadManifest.files", "A configuration-set payload file did not match its manifest hash.");
            }

            verifiedFiles++;
        }

        return new PayloadVerificationResult(true, verifiedFiles, []);
    }

    private static IReadOnlyList<DeploymentValidationError> ValidateEntries(IReadOnlyList<DeploymentPackageManifestEntry> entries)
    {
        var errors = new List<DeploymentValidationError>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (!IsSafeRelativePath(entry.RelativePath) || !paths.Add(entry.RelativePath) ||
                entry.Length < 0 || entry.Sha256.Length != 64 || !entry.Sha256.All(Uri.IsHexDigit))
            {
                errors.Add(new DeploymentValidationError(
                    "deployment.payload.manifest.entry.invalid",
                    "payloadManifest.files",
                    "Configuration-set payload manifest contains an invalid file entry."));
            }
        }

        return errors;
    }

    private static bool IsSafeRelativePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        !WindowsPathFormat.IsAbsolute(path) &&
        !path.Split(['/', '\\']).Any(static segment => segment is "" or "." or "..");

    private static string GetContainedFilePath(string rootDirectory, string relativePath)
    {
        var filePath = Path.GetFullPath(Path.Combine(rootDirectory, relativePath));
        var rootWithSeparator = rootDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? rootDirectory
            : rootDirectory + Path.DirectorySeparatorChar;
        return filePath.StartsWith(rootWithSeparator, StringComparison.Ordinal)
            ? filePath
            : throw new InvalidOperationException("Payload file path escaped its root directory.");
    }

    private static PayloadVerificationResult Failure(string code, string fieldPath, string message) =>
        new(false, 0, [new DeploymentValidationError(code, fieldPath, message)]);
}
