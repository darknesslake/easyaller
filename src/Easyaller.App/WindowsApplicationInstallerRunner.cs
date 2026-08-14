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

    public ApplicationProcessResult Run(ApplicationInstallStep step, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (!OperatingSystem.IsWindows())
        {
            return new ApplicationProcessResult(null, "Установка приложений доступна только в Windows.");
        }

        try
        {
            var installerLogPath = TryCreateInstallerLogPath(step);
            using var process = new Process { StartInfo = CreateStartInfo(step, installerLogPath) };
            if (!process.Start())
            {
                return new ApplicationProcessResult(null, "Не удалось запустить установщик.");
            }


            if (cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                return new ApplicationProcessResult(null, "Установка остановлена пользователем.", WasCancelled: true);
            }

            var deadline = DateTime.UtcNow + _timeout;
            while (!process.WaitForExit(250))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    TryKill(process);
                    return new ApplicationProcessResult(null, "Установка остановлена пользователем.", WasCancelled: true);
                }

                if (DateTime.UtcNow >= deadline)
                {
                    // A hung installer must not block the whole run forever.
                    TryKill(process);
                    return new ApplicationProcessResult(null, $"Установщик не завершился за {_timeout.TotalMinutes:0} мин и был остановлен.");
                }
            }

            var errorMessage = process.ExitCode == 0 || process.ExitCode is 1641 or 3010
                ? null
                : installerLogPath is null
                    ? null
                    : File.Exists(installerLogPath)
                        ? $"Журнал установщика сохранён: {installerLogPath}"
                        : $"Установщик не создал запрошенный журнал: {installerLogPath}";
            return new ApplicationProcessResult(process.ExitCode, errorMessage);
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

    private static ProcessStartInfo CreateStartInfo(ApplicationInstallStep step, string? installerLogPath)
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

        if (installerLogPath is not null)
        {
            startInfo.ArgumentList.Add("/LOG=" + installerLogPath);
        }

        return startInfo;
    }

    private static string? TryCreateInstallerLogPath(ApplicationInstallStep step)
    {
        var detection = InstallerFrameworkDetector.Detect(step.ExecutablePath);
        if (detection.Framework != InstallerFramework.InnoSetup)
        {
            return null;
        }

        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Easyaller",
                "InstallerLogs");
            Directory.CreateDirectory(directory);
            var safeName = string.Concat(step.DisplayName.Select(static character =>
                Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
            return Path.Combine(directory, $"{DateTime.Now:yyyyMMdd-HHmmss}-{safeName}.log");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
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
