using System.ComponentModel;
using System.Diagnostics;
using Easyaller.Deployment;

namespace Easyaller.App;

/// <summary>
/// Starts one planned installer and waits for it. The file and its arguments come from the
/// validated plan, never from free-form text, and no shell is involved.
/// </summary>
public sealed class WindowsApplicationInstallerRunner : IApplicationInstallerRunner
{
    private readonly TimeSpan _timeout;

    public WindowsApplicationInstallerRunner(TimeSpan? timeout = null)
    {
        _timeout = timeout ?? TimeSpan.FromMinutes(30);
    }

    public ApplicationProcessResult Run(ApplicationInstallStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (!OperatingSystem.IsWindows())
        {
            return new ApplicationProcessResult(null, "Установка приложений доступна только в Windows.");
        }

        try
        {
            using var process = new Process { StartInfo = CreateStartInfo(step) };
            if (!process.Start())
            {
                return new ApplicationProcessResult(null, "Не удалось запустить установщик.");
            }

            if (!process.WaitForExit((int)_timeout.TotalMilliseconds))
            {
                // A hung installer must not block the whole run forever.
                TryKill(process);
                return new ApplicationProcessResult(null, $"Установщик не завершился за {_timeout.TotalMinutes:0} мин и был остановлен.");
            }

            return new ApplicationProcessResult(process.ExitCode, null);
        }
        catch (Win32Exception exception)
        {
            return new ApplicationProcessResult(null, exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return new ApplicationProcessResult(null, exception.Message);
        }
    }

    private static ProcessStartInfo CreateStartInfo(ApplicationInstallStep step)
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(step.ExecutablePath) ?? string.Empty,
        };

        // An .msi is data, not a program: it has to be handed to msiexec.
        if (string.Equals(Path.GetExtension(step.ExecutablePath), ".msi", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.FileName = "msiexec.exe";
            startInfo.ArgumentList.Add("/i");
            startInfo.ArgumentList.Add(step.ExecutablePath);
        }
        else
        {
            startInfo.FileName = step.ExecutablePath;
        }

        foreach (var argument in step.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // The process ended between the timeout and the kill; nothing left to stop.
        }
    }
}
