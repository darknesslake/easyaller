using System.Text;
using System.Xml.Linq;
using Easyaller.Core.Profiles;
using Easyaller.Deployment;

namespace Easyaller.Core.Tests;

public sealed class UnattendXmlGeneratorTests
{
    private const string UnattendNamespace = "urn:schemas-microsoft-com:unattend";

    [Fact]
    public void Generate_SameValidatedRequest_ProducesByteIdenticalXml()
    {
        var request = CreateRequest();
        var generator = new UnattendXmlGenerator();

        var first = generator.Generate(request);
        var second = generator.Generate(request);

        Assert.Equal(first, second);
        Assert.DoesNotContain(Encoding.UTF8.GetString(first), "\r", StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_ValidProfile_UsesOnlyDocumentedConfiguredSettings()
    {
        var defaultProfile = ProvisioningProfileFactory.CreateDefault();
        var profile = defaultProfile with
        {
            Windows = defaultProfile.Windows with
            {
                Locale = new LocaleSettings("ru-RU", "ru-RU", "ru-RU", "ru-RU"),
                TimeZone = "West Asia Standard Time",
                Oobe = new OobeSettings(true, true, false, false, 3),
            },
        };

        var xml = XDocument.Parse(Encoding.UTF8.GetString(new UnattendXmlGenerator().Generate(CreateRequest(profile))));
        var ns = XNamespace.Get(UnattendNamespace);

        var uiLanguages = xml.Descendants(ns + "UILanguage").ToArray();
        Assert.Equal(2, uiLanguages.Length);
        Assert.All(uiLanguages, language => Assert.Equal("ru-RU", language.Value));
        Assert.Equal("West Asia Standard Time", xml.Descendants(ns + "TimeZone").Single().Value);
        Assert.Equal("true", xml.Descendants(ns + "HideEULAPage").Single().Value);
        Assert.Equal("false", xml.Descendants(ns + "HideOnlineAccountScreens").Single().Value);
        Assert.Equal("true", xml.Descendants(ns + "HideWirelessSetupInOOBE").Single().Value);
        Assert.Equal("3", xml.Descendants(ns + "ProtectYourPC").Single().Value);
    }

    [Fact]
    public void Generate_TemporaryLocalAccount_UsesObfuscatedPasswordAndNoAutoLogon()
    {
        using var credential = new EphemeralLocalAccountCredential("ProvisioningAdmin", "safe<&password".AsSpan());
        var text = Encoding.UTF8.GetString(new UnattendXmlGenerator().Generate(CreateRequest(temporaryLocalAccount: credential)));
        var xml = XDocument.Parse(text);
        var ns = XNamespace.Get(UnattendNamespace);

        Assert.Equal("ProvisioningAdmin", xml.Descendants(ns + "Name").Single().Value);
        Assert.Equal("Administrators", xml.Descendants(ns + "Group").Single().Value);
        Assert.Equal("false", xml.Descendants(ns + "PlainText").Single().Value);
        Assert.DoesNotContain("safe<&password", text, StringComparison.Ordinal);
        Assert.DoesNotContain("AutoLogon", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_EscapesConfiguredValuesThroughTheXmlWriter()
    {
        var defaultProfile = ProvisioningProfileFactory.CreateDefault();
        var profile = defaultProfile with
        {
            Windows = defaultProfile.Windows with { TimeZone = "UTC & <test>" },
        };

        var text = Encoding.UTF8.GetString(new UnattendXmlGenerator().Generate(CreateRequest(profile)));
        var xml = XDocument.Parse(text);
        var ns = XNamespace.Get(UnattendNamespace);

        Assert.Contains("UTC &amp; &lt;test&gt;", text, StringComparison.Ordinal);
        Assert.Equal("UTC & <test>", xml.Descendants(ns + "TimeZone").Single().Value);
    }

    [Fact]
    public void Generate_InvalidOrProhibitedProfile_StopsBeforeWritingXml()
    {
        var defaultProfile = ProvisioningProfileFactory.CreateDefault();
        var profile = defaultProfile with
        {
            Domain = new DomainSettings(DomainMode.Required, CredentialHandling.PromptAtRuntime),
        };

        var exception = Assert.Throws<DeploymentGenerationException>(() =>
            new UnattendXmlGenerator().Generate(CreateRequest(profile)));

        Assert.Contains(exception.Errors, error => error.Code == "deployment.profile.domainJoin.required.forbidden");
    }

    [Fact]
    public void Generate_OmitsProhibitedSections()
    {
        var text = Encoding.UTF8.GetString(new UnattendXmlGenerator().Generate(CreateRequest()));
        var xml = XDocument.Parse(text);
        var ns = XNamespace.Get(UnattendNamespace);
        var oobeSystem = xml.Descendants(ns + "settings")
            .Single(settings => settings.Attribute("pass")?.Value == "oobeSystem");

        Assert.DoesNotContain(
            oobeSystem.Elements(ns + "component"),
            component => component.Attribute("name")?.Value == "Microsoft-Windows-Shell-Setup");
        Assert.DoesNotContain("DiskConfiguration", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ProductKey", text, StringComparison.Ordinal);
        Assert.DoesNotContain("DomainAccounts", text, StringComparison.Ordinal);
        Assert.DoesNotContain("FirstLogonCommands", text, StringComparison.Ordinal);
        Assert.DoesNotContain("RunSynchronous", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TemporaryLocalAccountCredential_Dispose_RedactsAndPreventsGeneration()
    {
        var credential = new EphemeralLocalAccountCredential("ProvisioningAdmin", "temporary-password".AsSpan());
        credential.Dispose();

        var exception = Assert.Throws<DeploymentGenerationException>(() =>
            new UnattendXmlGenerator().Generate(CreateRequest(temporaryLocalAccount: credential)));

        Assert.True(credential.IsDisposed);
        Assert.Equal("Ephemeral local account credential is redacted.", credential.ToString());
        Assert.Contains(exception.Errors, error => error.Code == "deployment.localAccount.disposed");
    }

    private static DeploymentPreparationRequest CreateRequest(
        ProvisioningProfile? profile = null,
        EphemeralLocalAccountCredential? temporaryLocalAccount = null) => new(
            profile ?? ProvisioningProfileFactory.CreateDefault(),
            new WindowsDeploymentTarget(WindowsEdition.Professional, WindowsArchitecture.Amd64, "24H2", 26100),
            temporaryLocalAccount);
}
