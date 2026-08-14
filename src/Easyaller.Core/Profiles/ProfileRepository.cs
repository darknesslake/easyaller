namespace Easyaller.Core.Profiles;

public interface IProfileRepository
{
    ProfileRepositoryListResult List();

    ProfileRepositoryReadResult Read(Guid profileId);

    ProfileRepositoryWriteResult Create(ProvisioningProfile profile);

    ProfileRepositoryWriteResult Update(ProvisioningProfile profile, int expectedRevision);

    ProfileRepositoryWriteResult Clone(Guid sourceProfileId, string? name = null);

    ProfileRepositoryDeleteResult Delete(Guid profileId, int expectedRevision);
}

public enum ProfileRepositoryStatus
{
    Success,
    NotFound,
    Conflict,
    Invalid,
    IoFailure,
}

public sealed record ProfileRepositoryListResult(
    IReadOnlyList<ProvisioningProfile> Profiles,
    IReadOnlyList<ProfileStorageIssue> Issues);

public sealed record ProfileRepositoryReadResult(
    ProfileRepositoryStatus Status,
    ProvisioningProfile? Profile,
    IReadOnlyList<ProfileValidationError> Errors)
{
    public static ProfileRepositoryReadResult Success(ProvisioningProfile profile) =>
        new(ProfileRepositoryStatus.Success, profile, []);

    public static ProfileRepositoryReadResult Failure(
        ProfileRepositoryStatus status,
        params ProfileValidationError[] errors) => new(status, null, errors);
}

public sealed record ProfileRepositoryWriteResult(
    ProfileRepositoryStatus Status,
    ProvisioningProfile? Profile,
    IReadOnlyList<ProfileValidationError> Errors);

public sealed record ProfileRepositoryDeleteResult(
    ProfileRepositoryStatus Status,
    IReadOnlyList<ProfileValidationError> Errors);

public sealed record ProfileStorageIssue(string FileName, string Code, string Message);

public enum AtomicWritePhase
{
    TemporaryFileWritten,
    BackupCreated,
    BeforeFinalize,
    Finalized,
}

public interface IProfileAtomicFileWriter
{
    void Write(string targetPath, ReadOnlySpan<byte> contents);
}

public sealed class AtomicProfileFileWriter(Action<AtomicWritePhase>? phaseObserver = null) : IProfileAtomicFileWriter
{
    public const string BackupExtension = ".backup.json";

    private readonly Action<AtomicWritePhase>? _phaseObserver = phaseObserver;

    public void Write(string targetPath, ReadOnlySpan<byte> contents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        var directory = Path.GetDirectoryName(targetPath)
            ?? throw new ArgumentException("Target path must include a directory.", nameof(targetPath));
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllBytes(temporaryPath, contents.ToArray());
            _phaseObserver?.Invoke(AtomicWritePhase.TemporaryFileWritten);

            if (File.Exists(targetPath))
            {
                File.Copy(targetPath, GetBackupPath(targetPath), overwrite: true);
                _phaseObserver?.Invoke(AtomicWritePhase.BackupCreated);
            }

            _phaseObserver?.Invoke(AtomicWritePhase.BeforeFinalize);
            File.Move(temporaryPath, targetPath, overwrite: true);
            _phaseObserver?.Invoke(AtomicWritePhase.Finalized);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public static string GetBackupPath(string targetPath) => targetPath + BackupExtension;
}

public sealed class FileProfileRepository : IProfileRepository
{
    public const string ProfileExtension = ".wpprofile.json";

    private readonly object _sync = new();
    private readonly string _rootDirectory;
    private readonly ProfileJsonSerializer _serializer;
    private readonly IProfileAtomicFileWriter _atomicWriter;

    public FileProfileRepository(
        string? rootDirectory = null,
        ProfileJsonSerializer? serializer = null,
        IProfileAtomicFileWriter? atomicWriter = null)
    {
        _rootDirectory = rootDirectory ?? GetDefaultRootDirectory();
        _serializer = serializer ?? new ProfileJsonSerializer();
        _atomicWriter = atomicWriter ?? new AtomicProfileFileWriter();
    }

    public string RootDirectory => _rootDirectory;

    public static string GetDefaultRootDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Easyaller",
        "Profiles");

    public ProfileRepositoryListResult List()
    {
        lock (_sync)
        {
            if (!Directory.Exists(_rootDirectory))
            {
                return new ProfileRepositoryListResult([], []);
            }

            var profiles = new List<ProvisioningProfile>();
            var issues = new List<ProfileStorageIssue>();

            foreach (var path in Directory.EnumerateFiles(_rootDirectory, $"*{ProfileExtension}", SearchOption.TopDirectoryOnly))
            {
                var fileName = Path.GetFileName(path);
                var profileIdText = fileName[..^ProfileExtension.Length];
                if (!Guid.TryParse(profileIdText, out var expectedProfileId))
                {
                    TryQuarantine(path);
                    issues.Add(new ProfileStorageIssue(
                        fileName,
                        "profile.storage.fileName.invalid",
                        "Profile file name must be its profile ID."));
                    continue;
                }

                var result = ReadFile(path, expectedProfileId, quarantineInvalidFile: true);
                if (result.Status == ProfileRepositoryStatus.Success)
                {
                    profiles.Add(result.Profile!);
                    continue;
                }

                issues.Add(new ProfileStorageIssue(
                    Path.GetFileName(path),
                    result.Errors.FirstOrDefault()?.Code ?? "profile.storage.unreadable",
                    result.Errors.FirstOrDefault()?.Message ?? "Profile file could not be read."));
            }

            return new ProfileRepositoryListResult(
                profiles
                    .OrderBy(static profile => profile.Metadata.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static profile => profile.ProfileId)
                    .ToArray(),
                issues);
        }
    }

    public ProfileRepositoryReadResult Read(Guid profileId)
    {
        if (profileId == Guid.Empty)
        {
            return Invalid("profile.id.required", "profileId", "Profile ID is required.");
        }

        lock (_sync)
        {
            var path = GetProfilePath(profileId);
            return File.Exists(path)
                ? ReadFile(path, profileId, quarantineInvalidFile: true)
                : ProfileRepositoryReadResult.Failure(ProfileRepositoryStatus.NotFound);
        }
    }

    public ProfileRepositoryWriteResult Create(ProvisioningProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        lock (_sync)
        {
            if (profile.ProfileId == Guid.Empty)
            {
                return WriteInvalid("profile.id.required", "profileId", "Profile ID is required.");
            }

            if (profile.Revision < 1)
            {
                // A profile new to this repository still carries a meaningful revision when it
                // arrives via import from another machine's edit history — only a truly invalid
                // (non-positive) number is rejected here, not "not equal to 1".
                return WriteInvalid("profile.storage.create.revision", "revision", "Revision must be at least 1.");
            }

            var targetPath = GetProfilePath(profile.ProfileId);
            if (File.Exists(targetPath))
            {
                return new ProfileRepositoryWriteResult(ProfileRepositoryStatus.Conflict, null, []);
            }

            return WriteProfile(profile, targetPath);
        }
    }

    public ProfileRepositoryWriteResult Update(ProvisioningProfile profile, int expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(profile);

        lock (_sync)
        {
            if (expectedRevision < 1)
            {
                return WriteInvalid("profile.storage.expectedRevision.invalid", "expectedRevision", "Expected revision must be at least 1.");
            }

            var existing = Read(profile.ProfileId);
            if (existing.Status != ProfileRepositoryStatus.Success)
            {
                return new ProfileRepositoryWriteResult(existing.Status, existing.Profile, existing.Errors);
            }

            if (existing.Profile!.Revision != expectedRevision)
            {
                return new ProfileRepositoryWriteResult(ProfileRepositoryStatus.Conflict, existing.Profile, []);
            }

            var updated = profile with { Revision = expectedRevision + 1 };
            return WriteProfile(updated, GetProfilePath(updated.ProfileId));
        }
    }

    public ProfileRepositoryWriteResult Clone(Guid sourceProfileId, string? name = null)
    {
        lock (_sync)
        {
            var source = Read(sourceProfileId);
            if (source.Status != ProfileRepositoryStatus.Success)
            {
                return new ProfileRepositoryWriteResult(source.Status, source.Profile, source.Errors);
            }

            var sourceProfile = source.Profile!;
            var cloneName = string.IsNullOrWhiteSpace(name)
                ? $"{sourceProfile.Metadata.Name} copy"
                : name.Trim();
            var clone = sourceProfile with
            {
                ProfileId = Guid.NewGuid(),
                Revision = 1,
                Metadata = sourceProfile.Metadata with { Name = cloneName },
            };

            return Create(clone);
        }
    }

    public ProfileRepositoryDeleteResult Delete(Guid profileId, int expectedRevision)
    {
        if (profileId == Guid.Empty)
        {
            return DeleteInvalid("profile.id.required", "profileId", "Profile ID is required.");
        }

        if (expectedRevision < 1)
        {
            return DeleteInvalid("profile.storage.expectedRevision.invalid", "expectedRevision", "Expected revision must be at least 1.");
        }

        lock (_sync)
        {
            var existing = Read(profileId);
            if (existing.Status != ProfileRepositoryStatus.Success)
            {
                return new ProfileRepositoryDeleteResult(existing.Status, existing.Errors);
            }

            if (existing.Profile!.Revision != expectedRevision)
            {
                return new ProfileRepositoryDeleteResult(ProfileRepositoryStatus.Conflict, []);
            }

            var targetPath = GetProfilePath(profileId);
            try
            {
                File.Copy(targetPath, AtomicProfileFileWriter.GetBackupPath(targetPath), overwrite: true);
                File.Delete(targetPath);
                return new ProfileRepositoryDeleteResult(ProfileRepositoryStatus.Success, []);
            }
            catch (IOException exception)
            {
                return DeleteIoFailure(exception);
            }
            catch (UnauthorizedAccessException exception)
            {
                return DeleteIoFailure(exception);
            }
        }
    }

    private ProfileRepositoryReadResult ReadFile(string path, Guid? expectedProfileId, bool quarantineInvalidFile)
    {
        try
        {
            var parsed = _serializer.Read(File.ReadAllBytes(path));
            if (!parsed.IsValid)
            {
                if (quarantineInvalidFile)
                {
                    TryQuarantine(path);
                }

                return new ProfileRepositoryReadResult(ProfileRepositoryStatus.Invalid, null, parsed.Errors);
            }

            if (expectedProfileId is not null && parsed.Profile!.ProfileId != expectedProfileId)
            {
                if (quarantineInvalidFile)
                {
                    TryQuarantine(path);
                }

                return Invalid("profile.storage.idMismatch", "profileId", "Profile ID does not match its storage file name.");
            }

            return ProfileRepositoryReadResult.Success(parsed.Profile!);
        }
        catch (IOException exception)
        {
            return IoFailure(exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            return IoFailure(exception);
        }
    }

    private ProfileRepositoryWriteResult WriteProfile(ProvisioningProfile profile, string targetPath)
    {
        try
        {
            _atomicWriter.Write(targetPath, _serializer.Serialize(profile));
            return new ProfileRepositoryWriteResult(ProfileRepositoryStatus.Success, profile, []);
        }
        catch (ProfileJsonException exception)
        {
            return WriteInvalid(exception.Code, exception.FieldPath, exception.Message);
        }
        catch (IOException exception)
        {
            return WriteIoFailure(exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            return WriteIoFailure(exception);
        }
    }

    private void TryQuarantine(string path)
    {
        try
        {
            var quarantineDirectory = Path.Combine(_rootDirectory, "Corrupted");
            Directory.CreateDirectory(quarantineDirectory);
            var quarantinePath = Path.Combine(
                quarantineDirectory,
                $"{Path.GetFileName(path)}.{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.{Guid.NewGuid():N}.invalid");
            File.Move(path, quarantinePath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string GetProfilePath(Guid profileId) => Path.Combine(_rootDirectory, profileId + ProfileExtension);

    private static ProfileRepositoryReadResult Invalid(string code, string fieldPath, string message) =>
        ProfileRepositoryReadResult.Failure(ProfileRepositoryStatus.Invalid, new ProfileValidationError(code, fieldPath, message));

    private static ProfileRepositoryReadResult IoFailure(Exception exception) =>
        ProfileRepositoryReadResult.Failure(
            ProfileRepositoryStatus.IoFailure,
            new ProfileValidationError("profile.storage.io", "profile", exception.Message));

    private static ProfileRepositoryWriteResult WriteInvalid(string code, string fieldPath, string message) =>
        new(ProfileRepositoryStatus.Invalid, null, [new ProfileValidationError(code, fieldPath, message)]);

    private static ProfileRepositoryWriteResult WriteIoFailure(Exception exception) =>
        new(ProfileRepositoryStatus.IoFailure, null, [new ProfileValidationError("profile.storage.io", "profile", exception.Message)]);

    private static ProfileRepositoryDeleteResult DeleteInvalid(string code, string fieldPath, string message) =>
        new(ProfileRepositoryStatus.Invalid, [new ProfileValidationError(code, fieldPath, message)]);

    private static ProfileRepositoryDeleteResult DeleteIoFailure(Exception exception) =>
        new(ProfileRepositoryStatus.IoFailure, [new ProfileValidationError("profile.storage.io", "profile", exception.Message)]);
}
