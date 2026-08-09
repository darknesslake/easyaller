using Easyaller.App;
using Easyaller.Core.Profiles;

namespace Easyaller.Core.Tests;

public sealed class ProfileEditorControllerTests
{
    [Fact]
    public void Save_ValidEdits_UpdatesTheProfileAndNormalizesDescription()
    {
        using var directory = new TemporaryDirectory();
        var repository = new FileProfileRepository(directory.Path);
        var original = ProvisioningProfileFactory.CreateDefault("Original profile");
        Assert.Equal(ProfileRepositoryStatus.Success, repository.Create(original).Status);
        var controller = new ProfileEditorController(repository);

        var result = controller.Save(original, "  Updated profile  ", "   ");

        Assert.Equal(ProfileRepositoryStatus.Success, result.Status);
        Assert.Equal(2, result.Profile!.Revision);
        Assert.Equal("Updated profile", result.Profile.Metadata.Name);
        Assert.Null(result.Profile.Metadata.Description);
    }

    [Fact]
    public void Save_EmptyName_ReturnsValidationErrorAndPreservesStoredProfile()
    {
        using var directory = new TemporaryDirectory();
        var repository = new FileProfileRepository(directory.Path);
        var original = ProvisioningProfileFactory.CreateDefault("Original profile");
        Assert.Equal(ProfileRepositoryStatus.Success, repository.Create(original).Status);
        var controller = new ProfileEditorController(repository);

        var result = controller.Save(original, "   ", null);

        Assert.Equal(ProfileRepositoryStatus.Invalid, result.Status);
        Assert.Equal("Original profile", repository.Read(original.ProfileId).Profile!.Metadata.Name);
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
