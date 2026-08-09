using System.Text;
using System.Xml;
using Easyaller.Core.Profiles;

namespace Easyaller.Deployment;

public sealed class UnattendXmlGenerator(DeploymentProfileValidator? profileValidator = null) : IUnattendGenerator
{
    private const string UnattendNamespace = "urn:schemas-microsoft-com:unattend";
    private const string WcmNamespace = "http://schemas.microsoft.com/WMIConfig/2002/State";
    private const string ProcessorArchitecture = "amd64";
    private const string PublicKeyToken = "31bf3856ad364e35";
    private const string ComponentLanguage = "neutral";
    private const string VersionScope = "nonSxS";
    private readonly DeploymentProfileValidator _profileValidator = profileValidator ?? new DeploymentProfileValidator();

    public byte[] Generate(DeploymentPreparationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var validation = _profileValidator.Validate(request);
        if (!validation.IsValid)
        {
            throw new DeploymentGenerationException(validation.Errors);
        }

        ValidateTemporaryLocalAccount(request.TemporaryLocalAccount);
        var firstLogonBootstrap = FirstLogonBootstrapper.CreatePlan(request);

        using var stream = new MemoryStream();
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = true,
            IndentChars = "  ",
            NewLineChars = "\n",
            NewLineHandling = NewLineHandling.Replace,
            OmitXmlDeclaration = false,
        };

        using (var writer = XmlWriter.Create(stream, settings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("unattend", UnattendNamespace);
            writer.WriteAttributeString("xmlns", "wcm", null, WcmNamespace);

            WriteWindowsPeSettings(writer, request.Profile.Windows.Locale);
            WriteSpecializeSettings(writer, request.Profile.Windows.TimeZone);
            WriteOobeSystemSettings(writer, request.Profile.Windows, request.TemporaryLocalAccount, firstLogonBootstrap);

            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        return stream.ToArray();
    }

    private static void WriteWindowsPeSettings(XmlWriter writer, LocaleSettings locale)
    {
        WriteSettings(writer, "windowsPE", () =>
        {
            WriteComponent(writer, "Microsoft-Windows-International-Core-WinPE", () => WriteLocale(writer, locale));
        });
    }

    private static void WriteSpecializeSettings(XmlWriter writer, string timeZone)
    {
        WriteSettings(writer, "specialize", () =>
        {
            WriteComponent(writer, "Microsoft-Windows-Shell-Setup", () => WriteElement(writer, "TimeZone", timeZone));
        });
    }

    private static void WriteOobeSystemSettings(
        XmlWriter writer,
        WindowsSettings windows,
        EphemeralLocalAccountCredential? temporaryLocalAccount,
        FirstLogonBootstrapPlan? firstLogonBootstrap)
    {
        WriteSettings(writer, "oobeSystem", () =>
        {
            WriteComponent(writer, "Microsoft-Windows-International-Core", () => WriteLocale(writer, windows.Locale));
            if (HasConfiguredOobeSetting(windows.Oobe) || temporaryLocalAccount is not null || firstLogonBootstrap is not null)
            {
                WriteComponent(writer, "Microsoft-Windows-Shell-Setup", () =>
                {
                    WriteOobe(writer, windows.Oobe);
                    if (temporaryLocalAccount is not null)
                    {
                        WriteLocalAccount(writer, temporaryLocalAccount);
                    }

                    if (firstLogonBootstrap is not null)
                    {
                        WriteFirstLogonBootstrap(writer, firstLogonBootstrap);
                    }
                });
            }
        });
    }

    private static void WriteSettings(XmlWriter writer, string pass, Action content)
    {
        writer.WriteStartElement("settings", UnattendNamespace);
        writer.WriteAttributeString("pass", pass);
        content();
        writer.WriteEndElement();
    }

    private static void WriteComponent(XmlWriter writer, string name, Action content)
    {
        writer.WriteStartElement("component", UnattendNamespace);
        writer.WriteAttributeString("name", name);
        writer.WriteAttributeString("processorArchitecture", ProcessorArchitecture);
        writer.WriteAttributeString("publicKeyToken", PublicKeyToken);
        writer.WriteAttributeString("language", ComponentLanguage);
        writer.WriteAttributeString("versionScope", VersionScope);
        content();
        writer.WriteEndElement();
    }

    private static void WriteLocale(XmlWriter writer, LocaleSettings locale)
    {
        WriteElement(writer, "InputLocale", locale.InputLocale);
        WriteElement(writer, "SystemLocale", locale.SystemLocale);
        WriteElement(writer, "UILanguage", locale.UiLanguage);
        WriteElement(writer, "UserLocale", locale.UserLocale);
    }

    private static void WriteOobe(XmlWriter writer, OobeSettings oobe)
    {
        if (!HasConfiguredOobeSetting(oobe))
        {
            return;
        }

        writer.WriteStartElement("OOBE", UnattendNamespace);
        WriteOptionalBoolean(writer, "HideEULAPage", oobe.HideEula);
        WriteOptionalBoolean(writer, "HideOnlineAccountScreens", oobe.HideOnlineAccountScreens);
        WriteOptionalBoolean(writer, "HideWirelessSetupInOOBE", oobe.HideWirelessSetup);
        if (oobe.ProtectYourPc is not null)
        {
            WriteElement(writer, "ProtectYourPC", oobe.ProtectYourPc.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        writer.WriteEndElement();
    }

    private static void WriteLocalAccount(XmlWriter writer, EphemeralLocalAccountCredential credential)
    {
        writer.WriteStartElement("UserAccounts", UnattendNamespace);
        writer.WriteStartElement("LocalAccounts", UnattendNamespace);
        writer.WriteStartElement("LocalAccount", UnattendNamespace);
        writer.WriteAttributeString("wcm", "action", WcmNamespace, "add");
        writer.WriteStartElement("Password", UnattendNamespace);
        WriteElement(writer, "Value", credential.GetObfuscatedPasswordValue());
        WriteElement(writer, "PlainText", "false");
        writer.WriteEndElement();
        WriteElement(writer, "Group", "Administrators");
        WriteElement(writer, "Name", credential.AccountName);
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteFirstLogonBootstrap(XmlWriter writer, FirstLogonBootstrapPlan bootstrap)
    {
        writer.WriteStartElement("FirstLogonCommands", UnattendNamespace);
        writer.WriteStartElement("SynchronousCommand", UnattendNamespace);
        writer.WriteAttributeString("wcm", "action", WcmNamespace, "add");
        WriteElement(writer, "CommandLine", bootstrap.CommandLine);
        WriteElement(writer, "Description", "Verify the Easyaller payload and launch the fixed bootstrapper.");
        WriteElement(writer, "Order", "1");
        WriteElement(writer, "RequiresUserInput", "false");
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static bool HasConfiguredOobeSetting(OobeSettings oobe) =>
        oobe.HideEula is not null ||
        oobe.HideWirelessSetup is not null ||
        oobe.HideOnlineAccountScreens is not null ||
        oobe.ProtectYourPc is not null;

    private static void WriteOptionalBoolean(XmlWriter writer, string elementName, bool? value)
    {
        if (value is not null)
        {
            WriteElement(writer, elementName, value.Value ? "true" : "false");
        }
    }

    private static void WriteElement(XmlWriter writer, string name, string value)
    {
        writer.WriteStartElement(name, UnattendNamespace);
        writer.WriteString(value);
        writer.WriteEndElement();
    }

    private static void ValidateTemporaryLocalAccount(EphemeralLocalAccountCredential? credential)
    {
        if (credential is null)
        {
            return;
        }

        if (credential.IsDisposed)
        {
            throw new DeploymentGenerationException(
                [new DeploymentValidationError(
                    "deployment.localAccount.disposed",
                    "temporaryLocalAccount",
                    "Temporary local-account credentials have already been disposed.")]);
        }

        var invalidCharacters = new[] { '/', '\\', '[', ']', ':', '|', '<', '>', '+', '=', ';', ',', '?', '*', '%', '@', '`' };
        if (credential.AccountName.Length > 256 ||
            credential.AccountName.Equals("NONE", StringComparison.OrdinalIgnoreCase) ||
            credential.AccountName.Any(char.IsControl) ||
            credential.AccountName.IndexOfAny(invalidCharacters) >= 0)
        {
            throw new DeploymentGenerationException(
                [new DeploymentValidationError(
                    "deployment.localAccount.name.invalid",
                    "temporaryLocalAccount.accountName",
                    "Temporary local-account name is not supported by Windows Setup.")]);
        }
    }
}

public sealed class DeploymentGenerationException(IReadOnlyList<DeploymentValidationError> errors)
    : Exception("Deployment answer-file generation was blocked by validation.")
{
    public IReadOnlyList<DeploymentValidationError> Errors { get; } = errors;
}
