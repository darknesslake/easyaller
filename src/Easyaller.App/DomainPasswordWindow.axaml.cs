using Avalonia.Controls;
using Avalonia.Interactivity;
using Easyaller.Core.Provisioning;

namespace Easyaller.App;

/// <summary>Collects a short-lived domain password only when the chosen run includes a domain join.</summary>
public sealed partial class DomainPasswordWindow : Window
{
    private readonly string _userName = string.Empty;

    public DomainPasswordWindow()
    {
        InitializeComponent();
    }

    public DomainPasswordWindow(string domainName, string userName)
        : this()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domainName);
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        _userName = userName;
        AccountText.Text = $"Компьютер «{Environment.MachineName}» будет присоединён к домену «{domainName}» под учётной записью «{userName}».";
        Opened += (_, _) => PasswordTextBox.Focus();
    }

    public static async Task<RuntimeDomainCredential?> AskAsync(Window owner, string domainName, string userName)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var window = new DomainPasswordWindow(domainName, userName);
        return await window.ShowDialog<RuntimeDomainCredential?>(owner);
    }

    private void Confirm_Click(object? sender, RoutedEventArgs e)
    {
        var password = PasswordTextBox.Text;
        PasswordTextBox.Text = string.Empty;
        if (string.IsNullOrWhiteSpace(password))
        {
            ErrorText.Text = "Введите пароль доменной учётной записи.";
            ErrorText.IsVisible = true;
            return;
        }

        Close(new RuntimeDomainCredential(_userName, password.AsSpan()));
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        PasswordTextBox.Text = string.Empty;
        Close(null);
    }
}
