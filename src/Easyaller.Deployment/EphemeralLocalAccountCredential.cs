using System.Text;

namespace Easyaller.Deployment;

public sealed class EphemeralLocalAccountCredential : IDisposable
{
    private char[] _password;

    public EphemeralLocalAccountCredential(string accountName, ReadOnlySpan<char> password)
    {
        AccountName = accountName?.Trim() ?? throw new ArgumentNullException(nameof(accountName));
        if (string.IsNullOrWhiteSpace(AccountName))
        {
            throw new ArgumentException("Local account name is required.", nameof(accountName));
        }

        if (password.IsEmpty)
        {
            throw new ArgumentException("Local account password is required.", nameof(password));
        }

        _password = password.ToArray();
    }

    public string AccountName { get; }

    public bool IsDisposed { get; private set; }

    public override string ToString() => "Ephemeral local account credential is redacted.";

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        Array.Clear(_password);
        _password = [];
        IsDisposed = true;
    }

    internal string GetObfuscatedPasswordValue()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return Convert.ToBase64String(Encoding.Unicode.GetBytes(new string(_password)));
    }
}
