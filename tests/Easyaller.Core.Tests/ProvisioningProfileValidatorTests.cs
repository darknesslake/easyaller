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
        Assert.Equal("en-US;ru-RU", profile.Windows.Locale.InputLocale);
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
    public void Validate_MultipleInputLocales_AreAcceptedWhenEveryLocaleIsKnown()
    {
        var defaultProfile = ProvisioningProfileFactory.CreateDefault();
        var profile = defaultProfile with
        {
            Windows = defaultProfile.Windows with
            {
                Locale = defaultProfile.Windows.Locale with { InputLocale = "en-US;ru-RU" },
            },
        };

        var result = _validator.Validate(profile);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_UnknownLocaleInInputLocales_ReturnsLocaleError()
    {
        var defaultProfile = ProvisioningProfileFactory.CreateDefault();
        var profile = defaultProfile with
        {
            Windows = defaultProfile.Windows with
            {
                Locale = defaultProfile.Windows.Locale with { InputLocale = "en-US;not-a-real-locale" },
            },
        };

        var result = _validator.Validate(profile);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.FieldPath == "windows.locale.inputLocale");
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

    [Fact]
    public void Validate_StaticIpv4WithDns_IsValid()
    {
        var profile = ProvisioningProfileFactory.CreateDefault() with
        {
            Machine = ProvisioningProfileFactory.CreateDefault().Machine with
            {
                Network = StaticNetwork(),
            },
        };

        var result = _validator.Validate(profile);

        Assert.True(result.IsValid);
        Assert.Equal(24, ProvisioningProfileValidator.GetPrefixLength(profile.Machine.Network.StaticIpv4!.SubnetMask));
    }

    [Fact]
    public void Validate_StaticIpv4WithoutDns_IsValid()
    {
        var profile = ProvisioningProfileFactory.CreateDefault() with
        {
            Machine = ProvisioningProfileFactory.CreateDefault().Machine with
            {
                Network = new NetworkSettings(
                    NetworkConfigurationMode.StaticIpv4,
                    new StaticIpv4Configuration("192.0.2.77", "255.255.255.0", "192.0.2.254", [])),
            },
        };

        var result = _validator.Validate(profile);

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(@"\\server\share\installers", true)]
    [InlineData(@"D:\installers", true)]
    [InlineData("installers", false)]
    [InlineData(@"\\server\share\..\other", false)]
    public void Validate_ApplicationSourcePath_AcceptsFullPathsWithoutTraversal(string? sourcePath, bool expectedValid)
    {
        var profile = ProvisioningProfileFactory.CreateDefault() with { ApplicationSourcePath = sourcePath };

        var result = _validator.Validate(profile);

        Assert.Equal(
            expectedValid,
            !result.Errors.Any(static error => error.FieldPath == "applicationSourcePath"));
    }

    [Fact]
    public void Validate_StaticIpv4WithoutGateway_IsValid()
    {
        var profile = ProvisioningProfileFactory.CreateDefault() with
        {
            Machine = ProvisioningProfileFactory.CreateDefault().Machine with
            {
                Network = new NetworkSettings(
                    NetworkConfigurationMode.StaticIpv4,
                    new StaticIpv4Configuration("192.0.2.77", "255.255.255.0", string.Empty, ["192.0.2.53"])),
            },
        };

        var result = _validator.Validate(profile);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_StaticIpv4NetworkAddressWithoutGateway_ReturnsHostError()
    {
        var profile = ProvisioningProfileFactory.CreateDefault() with
        {
            Machine = ProvisioningProfileFactory.CreateDefault().Machine with
            {
                Network = new NetworkSettings(
                    NetworkConfigurationMode.StaticIpv4,
                    new StaticIpv4Configuration("192.0.2.0", "255.255.255.0", string.Empty, [])),
            },
        };

        var result = _validator.Validate(profile);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == "machine.network.staticIpv4.address.host.invalid");
    }

    [Fact]
    public void Validate_StaticIpv4WithFourDnsServers_ReturnsCountError()
    {
        var profile = ProvisioningProfileFactory.CreateDefault() with
        {
            Machine = ProvisioningProfileFactory.CreateDefault().Machine with
            {
                Network = new NetworkSettings(
                    NetworkConfigurationMode.StaticIpv4,
                    new StaticIpv4Configuration(
                        "192.0.2.77",
                        "255.255.255.0",
                        "192.0.2.254",
                        ["192.0.2.53", "198.51.100.53", "203.0.113.53", "192.0.2.54"])),
            },
        };

        var result = _validator.Validate(profile);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == "machine.network.staticIpv4.dnsServers.count.invalid");
    }

    [Fact]
    public void Validate_StaticIpv4WithInvalidGatewayAndDns_ReturnsFieldErrors()
    {
        var profile = ProvisioningProfileFactory.CreateDefault() with
        {
            Machine = ProvisioningProfileFactory.CreateDefault().Machine with
            {
                Network = new NetworkSettings(
                    NetworkConfigurationMode.StaticIpv4,
                    new StaticIpv4Configuration(
                        "192.0.2.77",
                        "255.0.255.0",
                        "192.0.3.254",
                        ["192.0.2.53", "192.0.2.53"])),
            },
        };

        var result = _validator.Validate(profile);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == "machine.network.staticIpv4.subnetMask.invalid");
        Assert.Contains(result.Errors, error => error.Code == "machine.network.staticIpv4.dnsServers.duplicate");
    }

    [Fact]
    public void Validate_PromptNetworkWithStaticValues_IsRejected()
    {
        var profile = ProvisioningProfileFactory.CreateDefault() with
        {
            Machine = ProvisioningProfileFactory.CreateDefault().Machine with
            {
                Network = new NetworkSettings(NetworkConfigurationMode.PromptAtRuntime, StaticNetwork().StaticIpv4),
            },
        };

        var result = _validator.Validate(profile);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == "machine.network.staticIpv4.unexpected");
    }

    [Fact]
    public void Validate_ProxyBypassListForRuntimeProxy_IsValid()
    {
        var defaultProfile = ProvisioningProfileFactory.CreateDefault();
        var profile = defaultProfile with
        {
            Machine = defaultProfile.Machine with
            {
                Proxy = new ProxySettings(
                    ProxyConfigurationMode.PromptAtRuntime,
                    ["*.example.test", "<local>", "192.0.2.53"]),
            },
        };

        var result = _validator.Validate(profile);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_ProxyBypassListWithUnsafeOrDuplicateEntries_IsRejected()
    {
        var defaultProfile = ProvisioningProfileFactory.CreateDefault();
        var profile = defaultProfile with
        {
            Machine = defaultProfile.Machine with
            {
                Proxy = new ProxySettings(
                    ProxyConfigurationMode.PromptAtRuntime,
                    ["*.example.test", "unsafe;entry", "*.EXAMPLE.TEST"]),
            },
        };

        var result = _validator.Validate(profile);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == "machine.proxy.bypassList.entry.invalid");
        Assert.Contains(result.Errors, error => error.Code == "machine.proxy.bypassList.entry.duplicate");
    }

    [Fact]
    public void Validate_ProxyBypassListWithoutRuntimeProxy_IsRejected()
    {
        var defaultProfile = ProvisioningProfileFactory.CreateDefault();
        var profile = defaultProfile with
        {
            Machine = defaultProfile.Machine with
            {
                Proxy = new ProxySettings(ProxyConfigurationMode.NotConfigured, ["*.example.test"]),
            },
        };

        var result = _validator.Validate(profile);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == "machine.proxy.bypassList.unexpected");
    }

    private static NetworkSettings StaticNetwork() => new(
        NetworkConfigurationMode.StaticIpv4,
        new StaticIpv4Configuration(
            "192.0.2.77",
            "255.255.255.0",
            "192.0.2.254",
            ["192.0.2.53", "198.51.100.53", "203.0.113.53"]));
}
