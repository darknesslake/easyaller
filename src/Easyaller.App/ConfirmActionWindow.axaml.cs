using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Easyaller.App;

/// <summary>
/// Confirms an action that changes the running machine. The editor's quick actions use it so a
/// single click can no longer rename this PC, replace its addresses, or join it to a domain.
/// </summary>
public sealed partial class ConfirmActionWindow : Window
{
    public ConfirmActionWindow()
    {
        InitializeComponent();
    }

    private const string DefaultWarningHeadline = "Это действие изменит текущий компьютер прямо сейчас.";

    public ConfirmActionWindow(string title, string description, string consequence, string confirmLabel, string warningHeadline)
        : this()
    {
        TitleText.Text = title;
        DescriptionText.Text = description;
        ConsequenceText.Text = consequence;
        ConfirmButton.Content = confirmLabel;
        WarningHeadlineText.Text = warningHeadline;
    }

    public static async Task<bool> AskAsync(
        Window owner,
        string title,
        string description,
        string consequence,
        string confirmLabel = "Выполнить",
        string warningHeadline = DefaultWarningHeadline)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var window = new ConfirmActionWindow(title, description, consequence, confirmLabel, warningHeadline);
        return await window.ShowDialog<bool>(owner);
    }

    private void Confirm_Click(object? sender, RoutedEventArgs e) => Close(true);

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
