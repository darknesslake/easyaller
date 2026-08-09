using System.Security.Cryptography;

namespace Easyaller.Deployment;

public interface ITemporaryLocalAccountCredentialFactory
{
    GeneratedTemporaryLocalAccount Create();
}

public sealed class TemporaryLocalAccountCredentialFactory : ITemporaryLocalAccountCredentialFactory
{
    public const string DefaultAccountName = "ProvisioningAdmin";
    public const int PasswordLength = 24;

    private const string Uppercase = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string Lowercase = "abcdefghijkmnopqrstuvwxyz";
    private const string Digits = "23456789";
    private const string Symbols = "!#$%*+-=?@";
    private const string AllCharacters = Uppercase + Lowercase + Digits + Symbols;

    public GeneratedTemporaryLocalAccount Create()
    {
        var password = new char[PasswordLength];
        password[0] = GetRandomCharacter(Uppercase);
        password[1] = GetRandomCharacter(Lowercase);
        password[2] = GetRandomCharacter(Digits);
        password[3] = GetRandomCharacter(Symbols);
        for (var index = 4; index < password.Length; index++)
        {
            password[index] = GetRandomCharacter(AllCharacters);
        }

        Shuffle(password);
        return new GeneratedTemporaryLocalAccount(DefaultAccountName, password);
    }

    private static char GetRandomCharacter(string characterSet) =>
        characterSet[RandomNumberGenerator.GetInt32(characterSet.Length)];

    private static void Shuffle(Span<char> characters)
    {
        for (var index = characters.Length - 1; index > 0; index--)
        {
            var otherIndex = RandomNumberGenerator.GetInt32(index + 1);
            (characters[index], characters[otherIndex]) = (characters[otherIndex], characters[index]);
        }
    }
}

public sealed class GeneratedTemporaryLocalAccount : IDisposable
{
    private char[] _passwordForDisplay;

    internal GeneratedTemporaryLocalAccount(string accountName, char[] password)
    {
        ArgumentNullException.ThrowIfNull(password);
        _passwordForDisplay = password;
        Credential = new EphemeralLocalAccountCredential(accountName, password);
    }

    public EphemeralLocalAccountCredential Credential { get; }

    public bool IsDisposed { get; private set; }

    public bool HasBeenRevealed { get; private set; }

    public override string ToString() => "Generated temporary local account is redacted.";

    public string? RevealPasswordOnce()
    {
        ThrowIfDisposed();
        if (HasBeenRevealed)
        {
            return null;
        }

        HasBeenRevealed = true;
        var password = new string(_passwordForDisplay);
        Array.Clear(_passwordForDisplay);
        _passwordForDisplay = [];
        return password;
    }

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        Array.Clear(_passwordForDisplay);
        _passwordForDisplay = [];
        Credential.Dispose();
        IsDisposed = true;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
    }
}
