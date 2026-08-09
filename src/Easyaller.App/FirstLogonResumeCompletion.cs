using System.Text;
using Easyaller.Deployment;
using Microsoft.Win32;

namespace Easyaller.App;

public interface IFirstLogonResumeCompletionStore
{
    void MarkCompleted();
}

public sealed class FirstLogonResumeCompletionService(IFirstLogonResumeCompletionStore? store = null)
{
    public const string ResumeArgument = "--resume";

    private readonly IFirstLogonResumeCompletionStore _store = store ?? new WindowsFirstLogonResumeCompletionStore();

    public bool TryComplete(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!arguments.Contains(ResumeArgument, StringComparer.Ordinal))
        {
            return false;
        }

        _store.MarkCompleted();
        return true;
    }
}

public sealed class WindowsFirstLogonResumeCompletionStore : IFirstLogonResumeCompletionStore
{
    private const string RunOnceKeyPath = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string RunOnceValueName = FirstLogonBootstrapper.RunOnceValueName;

    public void MarkCompleted()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var key = Registry.LocalMachine.OpenSubKey(RunOnceKeyPath, writable: true)
            ?? throw new InvalidOperationException("Easyaller RunOnce key is unavailable.");
        key.DeleteValue(RunOnceValueName, throwOnMissingValue: false);

        var stateDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Easyaller",
            "state");
        Directory.CreateDirectory(stateDirectory);
        File.WriteAllText(Path.Combine(stateDirectory, "bootstrap-state.json"), "{\"status\":\"completed\"}\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
