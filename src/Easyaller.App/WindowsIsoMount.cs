using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace Easyaller.App;

public sealed record IsoMountResult(string? Root, string? ErrorMessage)
{
    public bool IsMounted => !string.IsNullOrWhiteSpace(Root) && ErrorMessage is null;
}

/// <summary>
/// Mounts a Windows ISO read-only so its files can be used as a setup-media source without
/// extracting the image by hand. Nothing is written to the image, and an ISO that was already
/// attached by someone else is reused and left mounted on disposal.
/// </summary>
public sealed class WindowsIsoMount : IDisposable
{
    private const string MountScript = """
        $ErrorActionPreference = 'Stop'
        $isoPath = $env:EASYALLER_ISO_PATH
        $existing = Get-DiskImage -ImagePath $isoPath -ErrorAction SilentlyContinue
        $mountedHere = $false
        if ($existing -and $existing.Attached) {
            $image = $existing
        }
        else {
            $image = Mount-DiskImage -ImagePath $isoPath -Access ReadOnly -PassThru
            $mountedHere = $true
        }

        $volume = $image | Get-Volume | Where-Object { $null -ne $_.DriveLetter } | Select-Object -First 1
        if ($null -eq $volume) { throw 'Mounted ISO has no drive letter.' }

        [pscustomobject]@{
            Root = ([string]$volume.DriveLetter) + ':\'
            MountedHere = $mountedHere
        } | ConvertTo-Json -Compress
        """;

    private const string DismountScript = """
        $ErrorActionPreference = 'SilentlyContinue'
        Dismount-DiskImage -ImagePath $env:EASYALLER_ISO_PATH | Out-Null
        """;

    private string? _isoPathMountedHere;

    public IsoMountResult Mount(string isoPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(isoPath);
        if (!OperatingSystem.IsWindows())
        {
            return new IsoMountResult(null, "Монтирование ISO доступно только в Windows.");
        }

        Dismount();

        var (output, error, exitCode) = RunPowerShell(MountScript, isoPath);
        if (exitCode != 0)
        {
            return new IsoMountResult(null, string.IsNullOrWhiteSpace(error)
                ? "Не удалось смонтировать ISO."
                : error.Trim());
        }

        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement.TryGetProperty("Root", out var rootValue)
                ? rootValue.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(root))
            {
                return new IsoMountResult(null, "Смонтированный ISO не получил букву диска.");
            }

            if (document.RootElement.TryGetProperty("MountedHere", out var mountedHere)
                && mountedHere.ValueKind == JsonValueKind.True)
            {
                _isoPathMountedHere = isoPath;
            }

            return new IsoMountResult(root, null);
        }
        catch (JsonException)
        {
            return new IsoMountResult(null, "Не удалось прочитать результат монтирования ISO.");
        }
    }

    public void Dismount()
    {
        if (_isoPathMountedHere is null)
        {
            return;
        }

        RunPowerShell(DismountScript, _isoPathMountedHere);
        _isoPathMountedHere = null;
    }

    public void Dispose() => Dismount();

    private static (string Output, string Error, int ExitCode) RunPowerShell(string script, string isoPath)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                },
            };
            process.StartInfo.Environment["EASYALLER_ISO_PATH"] = isoPath;
            process.StartInfo.ArgumentList.Add("-NoLogo");
            process.StartInfo.ArgumentList.Add("-NoProfile");
            process.StartInfo.ArgumentList.Add("-NonInteractive");
            process.StartInfo.ArgumentList.Add("-Command");
            process.StartInfo.ArgumentList.Add(script);

            if (!process.Start())
            {
                return (string.Empty, "Не удалось запустить PowerShell.", 1);
            }

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return (output, error, process.ExitCode);
        }
        catch (Win32Exception)
        {
            return (string.Empty, "PowerShell недоступен.", 1);
        }
        catch (InvalidOperationException)
        {
            return (string.Empty, "Не удалось запустить PowerShell.", 1);
        }
    }
}
