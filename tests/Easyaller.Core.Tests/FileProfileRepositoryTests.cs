using Easyaller.Core.Profiles;

namespace Easyaller.Core.Tests;

public sealed class FileProfileRepositoryTests
{
    [Fact]
    public void CreateReadAndList_PersistsAProfileInTheConfiguredDirectory()
    {
        using var directory = new TemporaryDirectory();
        var repository = new FileProfileRepository(directory.Path);
        var profile = ProvisioningProfileFactory.CreateDefault("First profile");

        var created = repository.Create(profile);
        var read = repository.Read(profile.ProfileId);
        var listed = repository.List();

        Assert.Equal(ProfileRepositoryStatus.Success, created.Status);
        Assert.Equal(ProfileRepositoryStatus.Success, read.Status);
        Assert.Equal(profile.ProfileId, read.Profile!.ProfileId);
        Assert.Equal(profile.Metadata, read.Profile.Metadata);
        Assert.Equal([profile.ProfileId], listed.Profiles.Select(static storedProfile => storedProfile.ProfileId));
        Assert.Empty(listed.Issues);
        Assert.True(File.Exists(Path.Combine(directory.Path, profile.ProfileId + FileProfileRepository.ProfileExtension)));
    }

    [Fact]
    public void Update_WithStaleRevision_ReturnsCurrentProfileWithoutOverwritingIt()
    {
        using var directory = new TemporaryDirectory();
        var repository = new FileProfileRepository(directory.Path);
        var original = ProvisioningProfileFactory.CreateDefault("Original profile");
        Assert.Equal(ProfileRepositoryStatus.Success, repository.Create(original).Status);

        var firstUpdate = original with { Metadata = original.Metadata with { Name = "Updated profile" } };
        var saved = repository.Update(firstUpdate, expectedRevision: 1);
        var staleUpdate = original with { Metadata = original.Metadata with { Name = "Stale profile" } };
        var conflict = repository.Update(staleUpdate, expectedRevision: 1);

        Assert.Equal(ProfileRepositoryStatus.Success, saved.Status);
        Assert.Equal(2, saved.Profile!.Revision);
        Assert.Equal(ProfileRepositoryStatus.Conflict, conflict.Status);
        Assert.Equal(2, conflict.Profile!.Revision);
        Assert.Equal("Updated profile", repository.Read(original.ProfileId).Profile!.Metadata.Name);
        Assert.True(File.Exists(AtomicProfileFileWriter.GetBackupPath(
            Path.Combine(directory.Path, original.ProfileId + FileProfileRepository.ProfileExtension))));
    }

    [Fact]
    public void Clone_CreatesANewIdentityAtRevisionOne()
    {
        using var directory = new TemporaryDirectory();
        var repository = new FileProfileRepository(directory.Path);
        var source = ProvisioningProfileFactory.CreateDefault("Source profile");
        Assert.Equal(ProfileRepositoryStatus.Success, repository.Create(source).Status);

        var clone = repository.Clone(source.ProfileId, "Copied profile");

        Assert.Equal(ProfileRepositoryStatus.Success, clone.Status);
        Assert.NotEqual(source.ProfileId, clone.Profile!.ProfileId);
        Assert.Equal(1, clone.Profile.Revision);
        Assert.Equal("Copied profile", clone.Profile.Metadata.Name);
        Assert.Equal(2, repository.List().Profiles.Count);
    }

    [Fact]
    public void Delete_WithExpectedRevision_WritesABackupBeforeRemovingTheProfile()
    {
        using var directory = new TemporaryDirectory();
        var repository = new FileProfileRepository(directory.Path);
        var profile = ProvisioningProfileFactory.CreateDefault();
        Assert.Equal(ProfileRepositoryStatus.Success, repository.Create(profile).Status);

        var deleted = repository.Delete(profile.ProfileId, expectedRevision: 1);
        var targetPath = Path.Combine(directory.Path, profile.ProfileId + FileProfileRepository.ProfileExtension);

        Assert.Equal(ProfileRepositoryStatus.Success, deleted.Status);
        Assert.Equal(ProfileRepositoryStatus.NotFound, repository.Read(profile.ProfileId).Status);
        Assert.False(File.Exists(targetPath));
        Assert.True(File.Exists(AtomicProfileFileWriter.GetBackupPath(targetPath)));
    }

    [Fact]
    public void List_InvalidProfile_IsolatesTheFileAndReturnsAnIssue()
    {
        using var directory = new TemporaryDirectory();
        var invalidPath = Path.Combine(directory.Path, Guid.NewGuid() + FileProfileRepository.ProfileExtension);
        Directory.CreateDirectory(directory.Path);
        File.WriteAllText(invalidPath, "{ invalid json");
        var repository = new FileProfileRepository(directory.Path);

        var listed = repository.List();

        Assert.Empty(listed.Profiles);
        var issue = Assert.Single(listed.Issues);
        Assert.Equal("profile.json.invalid", issue.Code);
        Assert.False(File.Exists(invalidPath));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(directory.Path, "Corrupted")));
    }

    [Fact]
    public void Update_WhenFinalizationIsInterrupted_PreservesTheExistingProfileAndRemovesTheTemporaryFile()
    {
        using var directory = new TemporaryDirectory();
        var profile = ProvisioningProfileFactory.CreateDefault("Original profile");
        var initialRepository = new FileProfileRepository(directory.Path);
        Assert.Equal(ProfileRepositoryStatus.Success, initialRepository.Create(profile).Status);

        var interruptedRepository = new FileProfileRepository(
            directory.Path,
            atomicWriter: new AtomicProfileFileWriter(phase =>
            {
                if (phase == AtomicWritePhase.BeforeFinalize)
                {
                    throw new IOException("Simulated interrupted write.");
                }
            }));
        var interrupted = interruptedRepository.Update(
            profile with { Metadata = profile.Metadata with { Name = "Interrupted update" } },
            expectedRevision: 1);

        Assert.Equal(ProfileRepositoryStatus.IoFailure, interrupted.Status);
        Assert.Equal("Original profile", initialRepository.Read(profile.ProfileId).Profile!.Metadata.Name);
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));
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
