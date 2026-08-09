using System.Text;
using Easyaller.Core.Profiles;

namespace Easyaller.Core.Tests;

public sealed class ProfileImportExportServiceTests
{
    [Fact]
    public void ExportAndPreviewImport_RoundTripAValidProfileAndIdentifyConfidentialFields()
    {
        using var directory = new TemporaryDirectory();
        var service = CreateService(directory.Path);
        var profile = ProvisioningProfileFactory.CreateDefault() with
        {
            Metadata = new ProfileMetadata("Shared profile", "Internal standard"),
            Instructions = [new InstructionProfile("welcome", "Welcome", "Use the internal support portal.")],
        };
        var exportPath = Path.Combine(directory.Path, "shared.wpprofile.json");

        var preview = service.PreviewExport(profile);
        var exported = service.ExportToFile(profile, exportPath);
        var importedPreview = service.PreviewImport(File.ReadAllBytes(exportPath));

        Assert.True(exported.IsSuccess);
        Assert.Contains(preview.ConfidentialFields, field => field.FieldPath == "metadata.description");
        Assert.Contains(preview.ConfidentialFields, field => field.FieldPath == "instructions[0].content");
        Assert.Equal(ProfileImportPreviewStatus.Ready, importedPreview.Status);
        Assert.Equal(profile.ProfileId, importedPreview.Profile!.ProfileId);
    }

    [Fact]
    public void PreviewExport_StaticIpv4MarksNetworkSettingsAsConfidential()
    {
        using var directory = new TemporaryDirectory();
        var service = CreateService(directory.Path);
        var defaultProfile = ProvisioningProfileFactory.CreateDefault();
        var profile = defaultProfile with
        {
            Machine = defaultProfile.Machine with
            {
                Network = new NetworkSettings(
                    NetworkConfigurationMode.StaticIpv4,
                    new StaticIpv4Configuration("192.0.2.77", "255.255.255.0", "192.0.2.254", ["192.0.2.53"])),
            },
        };

        var preview = service.PreviewExport(profile);

        Assert.Contains(preview.ConfidentialFields, field => field.FieldPath == "machine.network.staticIpv4");
    }

    [Fact]
    public void PreviewExport_ProxyBypassListIsMarkedAsConfidential()
    {
        using var directory = new TemporaryDirectory();
        var service = CreateService(directory.Path);
        var defaultProfile = ProvisioningProfileFactory.CreateDefault();
        var profile = defaultProfile with
        {
            Machine = defaultProfile.Machine with
            {
                Proxy = new ProxySettings(ProxyConfigurationMode.PromptAtRuntime, ["*.example.test", "<local>"]),
            },
        };

        var preview = service.PreviewExport(profile);

        Assert.Contains(preview.ConfidentialFields, field => field.FieldPath == "machine.proxy.bypassList");
    }

    [Fact]
    public void PreviewImport_MalformedUtf8_IsRejectedBeforeDeserialization()
    {
        using var directory = new TemporaryDirectory();
        var service = CreateService(directory.Path);

        var preview = service.PreviewImport(new byte[] { 0x7B, 0xC3, 0x28, 0x7D });

        Assert.Equal(ProfileImportPreviewStatus.Invalid, preview.Status);
        Assert.Contains(preview.Errors, error => error.Code == "profile.import.utf8.invalid");
    }

    [Fact]
    public void PreviewImport_TooLargeFile_IsRejectedBeforeParsing()
    {
        using var directory = new TemporaryDirectory();
        var service = new ProfileImportExportService(new FileProfileRepository(directory.Path), maximumImportBytes: 16);

        var preview = service.PreviewImport(new byte[17]);

        Assert.Equal(ProfileImportPreviewStatus.Invalid, preview.Status);
        Assert.Contains(preview.Errors, error => error.Code == "profile.import.size.exceeded");
    }

    [Fact]
    public void PreviewImport_DuplicatePropertyAndFutureSchema_AreRejected()
    {
        using var directory = new TemporaryDirectory();
        var serializer = new ProfileJsonSerializer();
        var service = CreateService(directory.Path, serializer);
        var json = Encoding.UTF8.GetString(serializer.Serialize(ProvisioningProfileFactory.CreateDefault()));
        var duplicatePropertyJson = json.Replace(
            "\"profileId\":",
            "\"profileId\": \"00000000-0000-0000-0000-000000000001\", \"profileId\":",
            StringComparison.Ordinal);
        var futureSchemaJson = json.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal);

        var duplicatePreview = service.PreviewImport(Encoding.UTF8.GetBytes(duplicatePropertyJson));
        var futurePreview = service.PreviewImport(Encoding.UTF8.GetBytes(futureSchemaJson));

        Assert.Contains(duplicatePreview.Errors, error => error.Code == "profile.json.duplicateProperty");
        Assert.Contains(futurePreview.Errors, error => error.Code == "profile.schemaVersion.unsupported");
    }

    [Theory]
    [InlineData("password")]
    [InlineData("apiToken")]
    [InlineData("rawCommand")]
    public void PreviewImport_ForbiddenFields_AreRejectedWithTheirPath(string forbiddenField)
    {
        using var directory = new TemporaryDirectory();
        var serializer = new ProfileJsonSerializer();
        var service = CreateService(directory.Path, serializer);
        var json = Encoding.UTF8.GetString(serializer.Serialize(ProvisioningProfileFactory.CreateDefault()));
        var unsafeJson = json.Replace(
            "\"metadata\":",
            $"\"{forbiddenField}\": \"not allowed\",\n  \"metadata\":",
            StringComparison.Ordinal);

        var preview = service.PreviewImport(Encoding.UTF8.GetBytes(unsafeJson));

        Assert.Equal(ProfileImportPreviewStatus.Invalid, preview.Status);
        Assert.Equal("$." + forbiddenField, Assert.Single(preview.Errors).FieldPath);
    }

    [Fact]
    public void Import_ConflictCreateCopy_PreservesExistingProfileAndCreatesANewIdentity()
    {
        using var directory = new TemporaryDirectory();
        var repository = new FileProfileRepository(directory.Path);
        var service = new ProfileImportExportService(repository);
        var existing = ProvisioningProfileFactory.CreateDefault("Existing profile");
        Assert.Equal(ProfileRepositoryStatus.Success, repository.Create(existing).Status);
        var imported = existing with { Metadata = existing.Metadata with { Name = "Imported profile" } };
        var source = new ProfileJsonSerializer().Serialize(imported);

        var result = service.Import(source, ProfileImportConflictResolution.CreateCopy);

        Assert.Equal(ProfileImportStatus.Saved, result.Status);
        Assert.NotEqual(existing.ProfileId, result.Profile!.ProfileId);
        Assert.Equal(1, result.Profile.Revision);
        Assert.Equal("Existing profile", repository.Read(existing.ProfileId).Profile!.Metadata.Name);
        Assert.Equal(2, repository.List().Profiles.Count);
    }

    [Fact]
    public void Import_ConflictReplace_UpdatesTheExistingProfileWithANewRevision()
    {
        using var directory = new TemporaryDirectory();
        var repository = new FileProfileRepository(directory.Path);
        var service = new ProfileImportExportService(repository);
        var existing = ProvisioningProfileFactory.CreateDefault("Existing profile");
        Assert.Equal(ProfileRepositoryStatus.Success, repository.Create(existing).Status);
        var imported = existing with { Metadata = existing.Metadata with { Name = "Replacement profile" } };

        var result = service.Import(new ProfileJsonSerializer().Serialize(imported), ProfileImportConflictResolution.Replace);

        Assert.Equal(ProfileImportStatus.Saved, result.Status);
        Assert.Equal(existing.ProfileId, result.Profile!.ProfileId);
        Assert.Equal(2, result.Profile.Revision);
        Assert.Equal("Replacement profile", repository.Read(existing.ProfileId).Profile!.Metadata.Name);
    }

    [Fact]
    public void Import_Cancelled_DoesNotWriteThePreviewedProfile()
    {
        using var directory = new TemporaryDirectory();
        var repository = new FileProfileRepository(directory.Path);
        var service = new ProfileImportExportService(repository);
        var source = new ProfileJsonSerializer().Serialize(ProvisioningProfileFactory.CreateDefault());

        var result = service.Import(source, ProfileImportConflictResolution.Cancel);

        Assert.Equal(ProfileImportStatus.Cancelled, result.Status);
        Assert.Empty(repository.List().Profiles);
    }

    private static ProfileImportExportService CreateService(string repositoryPath, ProfileJsonSerializer? serializer = null) =>
        new(new FileProfileRepository(repositoryPath), serializer);

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
