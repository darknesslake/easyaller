using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Easyaller.App;

public sealed partial class DocumentationWindow : Window
{
    private const string DocumentationResourceName = "Easyaller.Docs.GettingStarted.ru.md";

    public DocumentationWindow() : this(0)
    {
    }

    public DocumentationWindow(int initialSection)
    {
        InitializeComponent();
        DocumentationSectionsList.SelectedIndex = Math.Clamp(initialSection, 0, 5);
        ShowSelectedSection();
    }

    private void DocumentationSectionsList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        => ShowSelectedSection();

    private void ShowSelectedSection()
    {
        if (QuickStartDocumentationPanel is null)
        {
            return;
        }

        var panels = new Control[]
        {
            QuickStartDocumentationPanel,
            ProfilesDocumentationPanel,
            ApplyDocumentationPanel,
            ApplicationsDocumentationPanel,
            MaintenanceDocumentationPanel,
            NewWindowsDocumentationPanel,
        };
        var selectedIndex = Math.Clamp(DocumentationSectionsList.SelectedIndex, 0, panels.Length - 1);
        for (var index = 0; index < panels.Length; index++)
        {
            panels[index].IsVisible = index == selectedIndex;
        }
    }

    public static string LoadDocumentation()
    {
        using var stream = typeof(DocumentationWindow).Assembly.GetManifestResourceStream(DocumentationResourceName);
        if (stream is null)
        {
            return "Встроенная документация не найдена. Переустановите Easyaller.";
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
