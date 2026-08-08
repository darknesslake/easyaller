using Easyaller.Core.Profiles;

namespace Easyaller.Core.Tests;

public sealed class ProvisioningProfileValidatorTests
{
    private readonly ProvisioningProfileValidator _validator = new();

    [Fact]
    public void Validate_DefaultProfile_IsValidAndHasNoCredentials()
    {
        var profile = ProvisioningProfileFactory.CreateDefault();

        var result = _validator.Validate(profile);

        Assert.True(result.IsValid);
        Assert.Equal(CredentialHandling.PromptAtRuntime, profile.Domain.Credentials);
        Assert.Equal(ComputerNameMode.RequiredAtRuntime, profile.Machine.ComputerName.Mode);
    }

    [Fact]
    public void Validate_OfflineSetupWithoutRequiredOobeSettings_ReturnsErrors()
    {
        var profile = ProvisioningProfileFactory.CreateDefault() with
        {
            Windows = ProvisioningProfileFactory.CreateDefault().Windows with
            {
                Oobe = new OobeSettings(null, false, false, true, null),
            },
        };

        var result = _validator.Validate(profile);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == "windows.oobe.offline.requiresWirelessHide");
        Assert.Contains(result.Errors, error => error.Code == "windows.oobe.offline.requiresOnlineAccountHide");
    }

    [Fact]
    public void Validate_UnsafeInstallerPath_ReturnsError()
    {
        var profile = ProvisioningProfileFactory.CreateDefault() with
        {
            Applications =
            [
                new ApplicationProfile(
                    "example-app",
                    "Example app",
                    ApplicationSourceKind.PackageRelative,
                    "../outside-package.exe",
                    []),
            ],
        };

        var result = _validator.Validate(profile);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == "applications.packagePath.unsafe");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-real-locale")]
    public void Validate_InvalidUiLanguage_ReturnsLocaleError(string uiLanguage)
    {
        var profile = ProvisioningProfileFactory.CreateDefault() with
        {
            Windows = ProvisioningProfileFactory.CreateDefault().Windows with
            {
                Locale = new LocaleSettings(uiLanguage, "en-US", "en-US", "en-US"),
            },
        };

        var result = _validator.Validate(profile);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.FieldPath == "windows.locale.uiLanguage");
    }

    [Fact]
    public void Validate_InvalidProfileIdentity_ReturnsStableErrors()
    {
        var profile = ProvisioningProfileFactory.CreateDefault() with
        {
            SchemaVersion = 99,
            ProfileId = Guid.Empty,
            Revision = 0,
            Metadata = new ProfileMetadata("", null),
        };

        var result = _validator.Validate(profile);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == "profile.schemaVersion.unsupported");
        Assert.Contains(result.Errors, error => error.Code == "profile.id.required");
        Assert.Contains(result.Errors, error => error.Code == "profile.revision.invalid");
        Assert.Contains(result.Errors, error => error.Code == "profile.name.required");
    }
}
