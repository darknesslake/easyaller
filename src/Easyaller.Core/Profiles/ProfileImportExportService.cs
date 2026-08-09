using System.Text;
using System.Text.Json;

namespace Easyaller.Core.Profiles;

public interface IProfileMigrationPipeline
{
    ProfileMigrationResult Apply(ReadOnlyMemory<byte> source);
}

public sealed class CurrentProfileMigrationPipeline : IProfileMigrationPipeline
{
    public ProfileMigrationResult Apply(ReadOnlyMemory<byte> source) => new(source, []);
}

public sealed record ProfileMigrationResult(
    ReadOnlyMemory<byte> Json,
    IReadOnlyList<ProfileValidationError> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

public enum ProfileImportPreviewStatus
{
    Ready,
    Conflict,
    Invalid,
    IoFailure,
}

public enum ProfileImportConflictResolution
{
    Cancel,
    Create,
    CreateCopy,
    Replace,
}

public enum ProfileImportStatus
{
    Saved,
    Cancelled,
    Conflict,
    Invalid,
    IoFailure,
}

public sealed record ProfileImportPreview(
    ProfileImportPreviewStatus Status,
    ProvisioningProfile? Profile,
    ProvisioningProfile? ExistingProfile,
    IReadOnlyList<ProfileConfidentialField> ConfidentialFields,
    IReadOnlyList<ProfileValidationError> Errors)
{
    public bool CanSave => Status is ProfileImportPreviewStatus.Ready or ProfileImportPreviewStatus.Conflict;
}

public sealed record ProfileImportResult(
    ProfileImportStatus Status,
    ProvisioningProfile? Profile,
    IReadOnlyList<ProfileValidationError> Errors);

public sealed record ProfileExportPreview(
    ProvisioningProfile Profile,
    IReadOnlyList<ProfileConfidentialField> ConfidentialFields);

public sealed record ProfileExportResult(
    bool IsSuccess,
    IReadOnlyList<ProfileValidationError> Errors);

public sealed record ProfileConfidentialField(string FieldPath, string Reason);

public sealed class ProfileImportExportService
{
    public const int DefaultMaximumImportBytes = 1_048_576;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly IProfileRepository _repository;
    private readonly ProfileJsonSerializer _serializer;
    private readonly IProfileMigrationPipeline _migrationPipeline;
    private readonly IProfileAtomicFileWriter _atomicWriter;
    private readonly int _maximumImportBytes;

    public ProfileImportExportService(
        IProfileRepository repository,
        ProfileJsonSerializer? serializer = null,
        IProfileMigrationPipeline? migrationPipeline = null,
        IProfileAtomicFileWriter? atomicWriter = null,
        int maximumImportBytes = DefaultMaximumImportBytes)
    {
        ArgumentNullException.ThrowIfNull(repository);
        if (maximumImportBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumImportBytes), "Maximum import size must be positive.");
        }

        _repository = repository;
        _serializer = serializer ?? new ProfileJsonSerializer();
        _migrationPipeline = migrationPipeline ?? new CurrentProfileMigrationPipeline();
        _atomicWriter = atomicWriter ?? new AtomicProfileFileWriter();
        _maximumImportBytes = maximumImportBytes;
    }

    public ProfileImportPreview PreviewImport(ReadOnlyMemory<byte> source)
    {
        if (source.Length > _maximumImportBytes)
        {
            return InvalidPreview(new ProfileValidationError(
                "profile.import.size.exceeded",
                "$",
                $"Profile import exceeds the {_maximumImportBytes}-byte limit."));
        }

        if (!IsValidUtf8(source))
        {
            return InvalidPreview(new ProfileValidationError(
                "profile.import.utf8.invalid",
                "$",
                "Profile import must be valid UTF-8."));
        }

        var migration = _migrationPipeline.Apply(source);
        if (!migration.IsValid)
        {
            return new ProfileImportPreview(ProfileImportPreviewStatus.Invalid, null, null, [], migration.Errors);
        }

        if (migration.Json.Length > _maximumImportBytes || !IsValidUtf8(migration.Json))
        {
            return InvalidPreview(new ProfileValidationError(
                "profile.import.migration.invalidOutput",
                "$",
                "Profile migration produced invalid import data."));
        }

        var forbiddenFields = ScanForbiddenFields(migration.Json);
        if (forbiddenFields.Count > 0)
        {
            return new ProfileImportPreview(ProfileImportPreviewStatus.Invalid, null, null, [], forbiddenFields);
        }

        var parsed = _serializer.Read(migration.Json.Span);
        if (!parsed.IsValid)
        {
            return new ProfileImportPreview(ProfileImportPreviewStatus.Invalid, null, null, [], parsed.Errors);
        }

        var profile = parsed.Profile!;
        var existing = _repository.Read(profile.ProfileId);
        return existing.Status switch
        {
            ProfileRepositoryStatus.Success => new ProfileImportPreview(
                ProfileImportPreviewStatus.Conflict,
                profile,
                existing.Profile,
                GetConfidentialFields(profile),
                []),
            ProfileRepositoryStatus.NotFound => new ProfileImportPreview(
                ProfileImportPreviewStatus.Ready,
                profile,
                null,
                GetConfidentialFields(profile),
                []),
            _ => new ProfileImportPreview(
                existing.Status == ProfileRepositoryStatus.IoFailure
                    ? ProfileImportPreviewStatus.IoFailure
                    : ProfileImportPreviewStatus.Invalid,
                null,
                null,
                [],
                existing.Errors),
        };
    }

    public ProfileImportResult Import(ReadOnlyMemory<byte> source, ProfileImportConflictResolution resolution)
    {
        var preview = PreviewImport(source);
        if (!preview.CanSave)
        {
            return new ProfileImportResult(ToImportStatus(preview.Status), null, preview.Errors);
        }

        if (resolution == ProfileImportConflictResolution.Cancel)
        {
            return new ProfileImportResult(ProfileImportStatus.Cancelled, null, []);
        }

        var profile = preview.Profile!;
        ProfileRepositoryWriteResult writeResult;
        if (preview.Status == ProfileImportPreviewStatus.Ready)
        {
            writeResult = resolution == ProfileImportConflictResolution.CreateCopy
                ? _repository.Create(profile with { ProfileId = Guid.NewGuid(), Revision = 1 })
                : _repository.Create(profile);
        }
        else if (resolution == ProfileImportConflictResolution.CreateCopy)
        {
            writeResult = _repository.Create(profile with { ProfileId = Guid.NewGuid(), Revision = 1 });
        }
        else if (resolution == ProfileImportConflictResolution.Replace)
        {
            var existing = preview.ExistingProfile!;
            writeResult = _repository.Update(
                profile with { Revision = existing.Revision },
                existing.Revision);
        }
        else
        {
            return new ProfileImportResult(
                ProfileImportStatus.Conflict,
                preview.ExistingProfile,
                [new ProfileValidationError(
                    "profile.import.conflict.resolutionRequired",
                    "profileId",
                    "An existing profile requires Create copy or Replace.")]);
        }

        return new ProfileImportResult(ToImportStatus(writeResult.Status), writeResult.Profile, writeResult.Errors);
    }

    public ProfileExportPreview PreviewExport(ProvisioningProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _ = _serializer.Serialize(profile);
        return new ProfileExportPreview(profile, GetConfidentialFields(profile));
    }

    public ProfileExportResult ExportToFile(ProvisioningProfile profile, string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        if (!destinationPath.EndsWith(FileProfileRepository.ProfileExtension, StringComparison.OrdinalIgnoreCase))
        {
            return ExportFailure(
                "profile.export.extension.invalid",
                "destinationPath",
                $"Export file name must end with {FileProfileRepository.ProfileExtension}.");
        }

        try
        {
            _atomicWriter.Write(destinationPath, _serializer.Serialize(profile));
            return new ProfileExportResult(true, []);
        }
        catch (ProfileJsonException exception)
        {
            return ExportFailure(exception.Code, exception.FieldPath, exception.Message);
        }
        catch (IOException exception)
        {
            return ExportFailure("profile.export.io", "destinationPath", exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            return ExportFailure("profile.export.io", "destinationPath", exception.Message);
        }
    }

    private static bool IsValidUtf8(ReadOnlyMemory<byte> source)
    {
        try
        {
            _ = StrictUtf8.GetString(source.Span);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static IReadOnlyList<ProfileValidationError> ScanForbiddenFields(ReadOnlyMemory<byte> source)
    {
        try
        {
            using var document = JsonDocument.Parse(source);
            var errors = new List<ProfileValidationError>();
            ScanElement(document.RootElement, "$", errors);
            return errors;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void ScanElement(JsonElement element, string path, ICollection<ProfileValidationError> errors)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var propertyPath = path + "." + property.Name;
                var normalizedName = NormalizeFieldName(property.Name);
                if (IsForbiddenSecretField(normalizedName) && propertyPath != "$.domain.credentials")
                {
                    errors.Add(new ProfileValidationError(
                        "profile.import.secretField.forbidden",
                        propertyPath,
                        "Profile import contains a forbidden secret-like field."));
                }
                else if (IsForbiddenCommandField(normalizedName))
                {
                    errors.Add(new ProfileValidationError(
                        "profile.import.commandField.forbidden",
                        propertyPath,
                        "Profile import contains a forbidden raw command field."));
                }

                ScanElement(property.Value, propertyPath, errors);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                ScanElement(item, $"{path}[{index}]", errors);
                index++;
            }
        }
    }

    private static IReadOnlyList<ProfileConfidentialField> GetConfidentialFields(ProvisioningProfile profile)
    {
        var fields = new List<ProfileConfidentialField>();
        if (!string.IsNullOrWhiteSpace(profile.Metadata.Description))
        {
            fields.Add(new ProfileConfidentialField(
                "metadata.description",
                "May describe internal workstation standards."));
        }

        if (profile.Machine.Network.StaticIpv4 is not null)
        {
            fields.Add(new ProfileConfidentialField(
                "machine.network.staticIpv4",
                "Contains organization-specific network settings."));
        }

        for (var index = 0; index < profile.Applications.Count; index++)
        {
            if (!string.IsNullOrWhiteSpace(profile.Applications[index].PackageRelativePath))
            {
                fields.Add(new ProfileConfidentialField(
                    $"applications[{index}].packageRelativePath",
                    "May reveal a private installer layout."));
            }
        }

        for (var index = 0; index < profile.Instructions.Count; index++)
        {
            if (!string.IsNullOrWhiteSpace(profile.Instructions[index].Content))
            {
                fields.Add(new ProfileConfidentialField(
                    $"instructions[{index}].content",
                    "May contain organization-specific guidance."));
            }
        }

        return fields;
    }

    private static bool IsForbiddenSecretField(string fieldName) => fieldName is
        "password" or "passwd" or "credential" or "credentials" or "token" or "accesstoken" or
        "refreshtoken" or "apitoken" or "apikey" or "secret" or "clientsecret" or "secretkey" or "privatekey";

    private static bool IsForbiddenCommandField(string fieldName) => fieldName is
        "command" or "rawcommand" or "script" or "powershell" or "shellcommand";

    private static string NormalizeFieldName(string value) => new(
        value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static ProfileImportPreview InvalidPreview(ProfileValidationError error) =>
        new(ProfileImportPreviewStatus.Invalid, null, null, [], [error]);

    private static ProfileImportStatus ToImportStatus(ProfileImportPreviewStatus status) => status switch
    {
        ProfileImportPreviewStatus.Invalid => ProfileImportStatus.Invalid,
        ProfileImportPreviewStatus.IoFailure => ProfileImportStatus.IoFailure,
        _ => ProfileImportStatus.Conflict,
    };

    private static ProfileImportStatus ToImportStatus(ProfileRepositoryStatus status) => status switch
    {
        ProfileRepositoryStatus.Success => ProfileImportStatus.Saved,
        ProfileRepositoryStatus.IoFailure => ProfileImportStatus.IoFailure,
        ProfileRepositoryStatus.Conflict => ProfileImportStatus.Conflict,
        _ => ProfileImportStatus.Invalid,
    };

    private static ProfileExportResult ExportFailure(string code, string fieldPath, string message) =>
        new(false, [new ProfileValidationError(code, fieldPath, message)]);
}
