using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Easyaller.App;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
            try
            {
                _ = new FirstLogonResumeCompletionService().TryComplete(Environment.GetCommandLineArgs());
            }
            catch
            {
                // Completion failure must not prevent Easyaller from opening.
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
