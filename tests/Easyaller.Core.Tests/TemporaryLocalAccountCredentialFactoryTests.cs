using System.Text;
using Easyaller.Core.Profiles;
using Easyaller.Deployment;

namespace Easyaller.Core.Tests;

public sealed class TemporaryLocalAccountCredentialFactoryTests
{
    [Fact]
    public void Create_GeneratesStrongPasswordAndRevealsItOnlyOnce()
    {
        using var generated = new TemporaryLocalAccountCredentialFactory().Create();

        var password = generated.RevealPasswordOnce();

        Assert.NotNull(password);
        Assert.Equal(TemporaryLocalAccountCredentialFactory.PasswordLength, password.Length);
        Assert.Contains(password, char.IsUpper);
        Assert.Contains(password, char.IsLower);
        Assert.Contains(password, char.IsDigit);
        Assert.Contains(password, character => "!#$%*+-=?@".Contains(character, StringComparison.Ordinal));
        Assert.Null(generated.RevealPasswordOnce());
        Assert.True(generated.HasBeenRevealed);
        Assert.Equal("Generated temporary local account is redacted.", generated.ToString());
        Assert.Equal("Ephemeral local account credential is redacted.", generated.Credential.ToString());
    }

    [Fact]
    public void Create_GeneratedCredentialCanBuildAnswerFileWithoutExposingItsPassword()
    {
        using var generated = new TemporaryLocalAccountCredentialFactory().Create();
        var request = new DeploymentPreparationRequest(
            ProvisioningProfileFactory.CreateDefault(),
            new WindowsDeploymentTarget(WindowsEdition.Professional, WindowsArchitecture.Amd64, "24H2", 26100),
            generated.Credential);

        var answerFile = Encoding.UTF8.GetString(new UnattendXmlGenerator().Generate(request));
        var passwordShownToAdministrator = generated.RevealPasswordOnce();

        Assert.Equal(TemporaryLocalAccountCredentialFactory.DefaultAccountName, generated.Credential.AccountName);
        Assert.NotNull(passwordShownToAdministrator);
        Assert.DoesNotContain(passwordShownToAdministrator, answerFile, StringComparison.Ordinal);
        Assert.DoesNotContain("AutoLogon", answerFile, StringComparison.Ordinal);
    }

    [Fact]
    public void Dispose_ZeroizesBothCredentialCopiesAndStopsFutureReveal()
    {
        var generated = new TemporaryLocalAccountCredentialFactory().Create();

        generated.Dispose();

        Assert.True(generated.IsDisposed);
        Assert.True(generated.Credential.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => generated.RevealPasswordOnce());
    }
}
