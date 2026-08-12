using System.Diagnostics;
using System.Text.Json;
using Easyaller.Core.Provisioning;

namespace Easyaller.App;

/// <summary>
/// One installed program as Windows itself reports it.
/// </summary>
public sealed record InstalledApplication(
    string DisplayName,
    string Version,
    string Publisher,
    string InstallLocation)
{
    public long SizeBytes { get; init; }

    public int FileCount { get; init; }
}

/// <summary>
/// A shortcut found on a desktop or in the Start menu.
/// </summary>
public sealed record InstalledShortcut(string Name, string Location, string TargetPath);

public sealed record InstalledApplicationSnapshot(
    IReadOnlyList<InstalledApplication> Applications,
    IReadOnlyList<InstalledShortcut> Shortcuts)
{
    /// <summary>
    /// Converts the Windows-specific reading into the neutral shape the checker compares.
    /// Folder sizes are measured only for the names passed in, because walking every installation
    /// folder on the machine would take far too long.
    /// </summary>
    public InstalledSoftwareSnapshot ToCoreSnapshot(IReadOnlyCollection<string>? measureFor = null) => new(
        Applications
            .Select(application =>
            {
                var shouldMeasure = measureFor is not null
                    && measureFor.Any(name => Matches(application.DisplayName, name));
                var (size, count) = shouldMeasure
                    ? InstalledApplicationInspector.MeasureInstallLocation(application.InstallLocation)
                    : (0L, 0);
                return new InstalledSoftwareEntry(
                    application.DisplayName,
                    application.Version,
                    application.InstallLocation,
                    size,
                    count);
            })
            .ToArray(),
        Shortcuts.Select(static shortcut => new InstalledShortcutEntry(shortcut.Name, shortcut.TargetPath)).ToArray());

    private static bool Matches(string candidate, string expected) =>
        !string.IsNullOrWhiteSpace(candidate)
        && !string.IsNullOrWhiteSpace(expected)
        && (candidate.Contains(expected, StringComparison.OrdinalIgnoreCase)
            || expected.Contains(candidate, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Reads which programs are installed and which shortcuts exist.
/// It reads the documented uninstall registry keys rather than Win32_Product, because querying
/// Win32_Product makes Windows reconfigure every installed MSI — slow and genuinely disruptive.
/// Everything here is read-only and needs no administrator rights.
/// </summary>
public sealed class InstalledApplicationInspector
{
    private const string ReadInstalledScript = """
        $ErrorActionPreference = 'SilentlyContinue'

        $uninstallKeys = @(
            'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
            'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
            'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*'
        )

        $applications = @(
            Get-ItemProperty -Path $uninstallKeys |
                Where-Object { $_.DisplayName -and -not $_.SystemComponent -and -not $_.ParentKeyName } |
                ForEach-Object {
                    [ordered]@{
                        displayName = [string]$_.DisplayName
                        version = [string]$_.DisplayVersion
                        publisher = [string]$_.Publisher
                        installLocation = [string]$_.InstallLocation
                    }
                }
        )

        $shortcutFolders = @(
            [Environment]::GetFolderPath('Desktop'),
            [Environment]::GetFolderPath('CommonDesktopDirectory'),
            [Environment]::GetFolderPath('Programs'),
            [Environment]::GetFolderPath('CommonPrograms')
        ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -Unique

        $shell = New-Object -ComObject WScript.Shell
        $shortcuts = @(
            foreach ($folder in $shortcutFolders) {
                Get-ChildItem -Path $folder -Filter *.lnk -Recurse -ErrorAction SilentlyContinue |
                    ForEach-Object {
                        $target = ''
                        try { $target = [string]$shell.CreateShortcut($_.FullName).TargetPath } catch { }
                        [ordered]@{
                            name = [System.IO.Path]::GetFileNameWithoutExtension($_.Name)
                            location = $_.DirectoryName
                            targetPath = $target
                        }
                    }
            }
        )

        [ordered]@{ applications = $applications; shortcuts = $shortcuts } | ConvertTo-Json -Depth 4 -Compress
        """;

    public InstalledApplicationSnapshot? Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(ReadInstalledScript);

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            // Enumerating every shortcut can take a while on a busy machine.
            if (!process.WaitForExit(120_000) || string.IsNullOrWhiteSpace(output))
            {
                return null;
            }

            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            return new InstalledApplicationSnapshot(
                ReadApplications(root),
                ReadShortcuts(root));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static IReadOnlyList<InstalledApplication> ReadApplications(JsonElement root)
    {
        var applications = new List<InstalledApplication>();
        foreach (var element in EnumerateArray(root, "applications"))
        {
            var displayName = GetString(element, "displayName");
            if (!string.IsNullOrWhiteSpace(displayName))
            {
                applications.Add(new InstalledApplication(
                    displayName,
                    GetString(element, "version"),
                    GetString(element, "publisher"),
                    GetString(element, "installLocation")));
            }
        }

        return applications;
    }

    /// <summary>
    /// Measures an installation folder. Walking a large tree is slow, so this is only called for
    /// the applications a profile actually cares about, never for everything installed.
    /// </summary>
    public static (long SizeBytes, int FileCount) MeasureInstallLocation(string installLocation)
    {
        if (string.IsNullOrWhiteSpace(installLocation) || !Directory.Exists(installLocation))
        {
            return (0, 0);
        }

        long size = 0;
        var count = 0;
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };
            foreach (var file in Directory.EnumerateFiles(installLocation, "*", options))
            {
                try
                {
                    size += new FileInfo(file).Length;
                    count++;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // A single unreadable file must not void the whole measurement.
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return (0, 0);
        }

        return (size, count);
    }

    private static IReadOnlyList<InstalledShortcut> ReadShortcuts(JsonElement root)
    {
        var shortcuts = new List<InstalledShortcut>();
        foreach (var element in EnumerateArray(root, "shortcuts"))
        {
            var name = GetString(element, "name");
            if (!string.IsNullOrWhiteSpace(name))
            {
                shortcuts.Add(new InstalledShortcut(
                    name,
                    GetString(element, "location"),
                    GetString(element, "targetPath")));
            }
        }

        return shortcuts;
    }

    private static IEnumerable<JsonElement> EnumerateArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            yield break;
        }

        // PowerShell collapses a single-element array into an object.
        if (value.ValueKind == JsonValueKind.Object)
        {
            yield return value;
            yield break;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var element in value.EnumerateArray())
        {
            yield return element;
        }
    }

    private static string GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
