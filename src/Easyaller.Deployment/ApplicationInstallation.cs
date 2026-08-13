using Easyaller.Core.Profiles;

namespace Easyaller.Deployment;

public enum ApplicationInstallOutcome
{
    Installed,
    InstalledRestartRequired,
    Failed,
    Skipped,
    NotRun,
}

public sealed record ApplicationInstallStep(
    string ApplicationId,
    string DisplayName,
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string RelativePath = "");

public sealed record ApplicationInstallPlan(
    string PackageRootDirectory,
    IReadOnlyList<ApplicationInstallStep> Steps,
    IReadOnlyList<ApplicationProfile> ManualApplications,
    IReadOnlyList<DeploymentValidationError> Errors)
{
    /// <summary>Installers left out because they target the other Windows architecture.</summary>
    public IReadOnlyList<ApplicationProfile> SkippedByArchitecture { get; init; } = [];

    public bool CanRun => Errors.Count == 0 && Steps.Count > 0;
}

public sealed record ApplicationInstallStepResult(
    ApplicationInstallStep Step,
    ApplicationInstallOutcome Outcome,
    int? ExitCode,
    string? ErrorMessage);

public sealed record ApplicationInstallReport(
    IReadOnlyList<ApplicationInstallStepResult> Results,
    bool StoppedOnFailure)
{
    public bool RequiresRestart => Results.Any(static result => result.Outcome == ApplicationInstallOutcome.InstalledRestartRequired);

    public int InstalledCount => Results.Count(static result =>
        result.Outcome is ApplicationInstallOutcome.Installed or ApplicationInstallOutcome.InstalledRestartRequired);
}

public sealed record ApplicationProcessResult(int? ExitCode, string? ErrorMessage);

/// <summary>One installer found by scanning a folder, ready to become a profile entry.</summary>
public sealed record DiscoveredInstaller(string RelativePath, string SuggestedName);

public enum InstallerFramework
{
    Unknown,
    Msi,
    Nsis,
    InnoSetup,
    InstallShield,
    WixBurn,
}

/// <summary>A guess about which installer framework built a file, offered as a starting point only.</summary>
public sealed record InstallerFrameworkDetection(
    InstallerFramework Framework,
    IReadOnlyList<string> SuggestedArguments,
    string FrameworkName);

/// <summary>
/// Recognizes common installer frameworks from plain-text markers most of them embed near the
/// start of the file — the same technique `strings installer.exe | grep "Inno Setup"` relies on.
/// This is a heuristic for a starting suggestion, never a guarantee: an operator must still review
/// the result before an unattended run depends on it.
/// </summary>
public static class InstallerFrameworkDetector
{
    // Signature text lives in the stub loader, not deep inside the compressed payload, so only the
    // start of the file needs reading even for a multi-hundred-megabyte installer.
    private const int ScanLimitBytes = 4 * 1024 * 1024;

    public static InstallerFrameworkDetection Detect(string filePath)
    {
        if (string.Equals(Path.GetExtension(filePath), ".msi", StringComparison.OrdinalIgnoreCase))
        {
            return new InstallerFrameworkDetection(InstallerFramework.Msi, ["/qn"], "Windows Installer (.msi)");
        }

        byte[] buffer;
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var length = (int)Math.Min(stream.Length, ScanLimitBytes);
            buffer = new byte[length];
            stream.ReadExactly(buffer);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Unknown();
        }

        return DetectFromContent(buffer);
    }

    public static InstallerFrameworkDetection DetectFromContent(ReadOnlySpan<byte> content)
    {
        if (Contains(content, ".wixburn"))
        {
            return new InstallerFrameworkDetection(InstallerFramework.WixBurn, ["/quiet", "/norestart"], "WiX Burn bootstrapper");
        }

        if (Contains(content, "Inno Setup"))
        {
            return new InstallerFrameworkDetection(InstallerFramework.InnoSetup, ["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART"], "Inno Setup");
        }

        if (Contains(content, "NullsoftInst") || Contains(content, "Nullsoft Install System") || Contains(content, "NSIS Error"))
        {
            return new InstallerFrameworkDetection(InstallerFramework.Nsis, ["/S"], "NSIS");
        }

        if (Contains(content, "InstallShield"))
        {
            // InstallShield forwards to its embedded MSI as one literal token: /v"/qn".
            return new InstallerFrameworkDetection(InstallerFramework.InstallShield, ["/s", "/v\"/qn\""], "InstallShield");
        }

        return Unknown();
    }

    private static InstallerFrameworkDetection Unknown() =>
        new(InstallerFramework.Unknown, [], string.Empty);

    private static bool Contains(ReadOnlySpan<byte> haystack, string asciiNeedle) =>
        haystack.IndexOf(System.Text.Encoding.ASCII.GetBytes(asciiNeedle)) >= 0;
}

public sealed record ApplicationStagingResult(
    string StagedRootDirectory,
    int CopiedFileCount,
    IReadOnlyList<DeploymentValidationError> Errors)
{
    public bool IsStaged => Errors.Count == 0 && CopiedFileCount > 0;
}

public interface IApplicationInstallerRunner
{
    ApplicationProcessResult Run(ApplicationInstallStep step);
}

/// <summary>
/// Turns the declarative application list into an ordered install run.
/// Planning resolves every path inside the chosen package directory; nothing is executed here.
/// </summary>
public sealed class ApplicationInstallationService
{
    // Windows installers use these to report "done, but the machine must reboot".
    private const int ExitCodeRestartRequired = 3010;
    private const int ExitCodeRestartInitiated = 1641;

    /// <summary>
    /// Copies and installs at the same time: the copier keeps pulling the next installer from the
    /// share while the current one is being installed, so slow network transfers overlap with slow
    /// installs. Installs still happen strictly one at a time, in profile order.
    /// Installing straight from a share is avoided on purpose — the link can drop mid-install and
    /// some installers reject UNC paths — so every install runs from the local copy.
    /// </summary>
    public async Task<ApplicationInstallReport> RunPipelinedAsync(
        ApplicationInstallPlan plan,
        string destinationDirectory,
        IApplicationInstallerRunner runner,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(runner);

        var results = new List<ApplicationInstallStepResult>();
        if (!plan.CanRun)
        {
            return new ApplicationInstallReport(results, StoppedOnFailure: false);
        }

        var destinationRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationDirectory));
        var steps = plan.Steps;
        var copies = steps.Select(static _ => new TaskCompletionSource<CopyOutcome>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();

        using var stopCopying = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var copyLoop = Task.Run(
            async () =>
            {
                for (var index = 0; index < steps.Count; index++)
                {
                    if (stopCopying.IsCancellationRequested)
                    {
                        copies[index].TrySetResult(new CopyOutcome(null, "Копирование отменено."));
                        continue;
                    }

                    progress?.Report($"Копируется: {steps[index].DisplayName}");
                    var outcome = await CopyAsync(steps[index], destinationRoot, stopCopying.Token).ConfigureAwait(false);
                    copies[index].TrySetResult(outcome);
                }
            },
            CancellationToken.None);

        var stopped = false;
        for (var index = 0; index < steps.Count; index++)
        {
            var step = steps[index];
            if (stopped)
            {
                results.Add(new ApplicationInstallStepResult(step, ApplicationInstallOutcome.NotRun, null, null));
                continue;
            }

            var copy = await copies[index].Task.ConfigureAwait(false);
            if (copy.LocalPath is null)
            {
                results.Add(new ApplicationInstallStepResult(step, ApplicationInstallOutcome.Failed, null, copy.ErrorMessage));
                stopped = true;
                await stopCopying.CancelAsync().ConfigureAwait(false);
                continue;
            }

            progress?.Report($"Устанавливается: {step.DisplayName}");
            var localStep = step with { ExecutablePath = copy.LocalPath };
            var processResult = await Task.Run(() => runner.Run(localStep), CancellationToken.None).ConfigureAwait(false);
            var outcome = DescribeOutcome(processResult);
            results.Add(new ApplicationInstallStepResult(localStep, outcome, processResult.ExitCode, processResult.ErrorMessage));
            if (outcome == ApplicationInstallOutcome.Failed)
            {
                stopped = true;
                // Nothing later will be installed, so stop pulling files over the network too.
                await stopCopying.CancelAsync().ConfigureAwait(false);
            }
        }

        await copyLoop.ConfigureAwait(false);
        return new ApplicationInstallReport(results, stopped);
    }

    private static async Task<CopyOutcome> CopyAsync(
        ApplicationInstallStep step,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        try
        {
            var relativePath = string.IsNullOrWhiteSpace(step.RelativePath)
                ? Path.GetFileName(step.ExecutablePath)
                : step.RelativePath;
            var destination = Path.GetFullPath(Path.Combine(destinationRoot, relativePath));
            if (!destination.StartsWith(destinationRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return new CopyOutcome(null, "Путь установщика выходит за пределы папки копирования.");
            }

            var relativeDirectory = Path.GetDirectoryName(relativePath);
            if (!string.IsNullOrWhiteSpace(relativeDirectory))
            {
                // Folder-based packages such as Office need every CAB/MSI beside setup.exe.
                var segments = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var packageRoot = Path.GetFullPath(step.ExecutablePath);
                foreach (var _ in segments)
                {
                    packageRoot = Path.GetDirectoryName(packageRoot)!;
                }

                var sourcePackageDirectory = Path.Combine(packageRoot, segments[0]);
                var destinationPackageDirectory = Path.Combine(destinationRoot, segments[0]);
                foreach (var sourceFile in Directory.EnumerateFiles(sourcePackageDirectory, "*", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relativeFile = Path.GetRelativePath(sourcePackageDirectory, sourceFile);
                    var destinationFile = Path.Combine(destinationPackageDirectory, relativeFile);
                    Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
                    await CopyFileAsync(sourceFile, destinationFile, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await CopyFileAsync(step.ExecutablePath, destination, cancellationToken).ConfigureAwait(false);
            }

            return new CopyOutcome(destination, null);
        }
        catch (OperationCanceledException)
        {
            return new CopyOutcome(null, "Копирование отменено.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new CopyOutcome(null, $"Не удалось скопировать {step.DisplayName}: {exception.Message}");
        }
    }

    private static async Task CopyFileAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        await using var target = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
    }

    private sealed record CopyOutcome(string? LocalPath, string? ErrorMessage);

    /// <summary>
    /// Builds the install queue for one folder. The architecture is a parameter so the decision can
    /// be tested; when omitted it is read from the running Windows.
    /// </summary>
    public ApplicationInstallPlan CreatePlan(
        ProvisioningProfile profile,
        string packageRootDirectory,
        ApplicationArchitecture? systemArchitecture = null)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var errors = new List<DeploymentValidationError>();
        var steps = new List<ApplicationInstallStep>();
        var manual = profile.Applications
            .Where(static application => application.SourceKind == ApplicationSourceKind.ExternalManual)
            .ToArray();
        var skippedByArchitecture = new List<ApplicationProfile>();

        if (string.IsNullOrWhiteSpace(packageRootDirectory) || !Path.IsPathFullyQualified(packageRootDirectory))
        {
            errors.Add(new DeploymentValidationError(
                "applications.packageRoot.invalid",
                "packageRootDirectory",
                "Choose the folder that contains the application installers."));
            return new ApplicationInstallPlan(packageRootDirectory ?? string.Empty, steps, manual, errors);
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageRootDirectory));
        if (!Directory.Exists(root))
        {
            errors.Add(new DeploymentValidationError(
                "applications.packageRoot.missing",
                "packageRootDirectory",
                "The selected installer folder does not exist."));
            return new ApplicationInstallPlan(root, steps, manual, errors);
        }

        var architecture = systemArchitecture ?? GetCurrentSystemArchitecture();
        foreach (var application in profile.Applications)
        {
            if (application.SourceKind != ApplicationSourceKind.PackageRelative)
            {
                continue;
            }

            // A profile may hold both builds of the same product; only the matching one is installed.
            if (application.Architecture != ApplicationArchitecture.Any && application.Architecture != architecture)
            {
                skippedByArchitecture.Add(application);
                continue;
            }

            if (string.IsNullOrWhiteSpace(application.PackageRelativePath))
            {
                errors.Add(Error(application, "applications.install.path.missing", "Application has no package path."));
                continue;
            }

            var candidate = Path.GetFullPath(Path.Combine(root, application.PackageRelativePath));

            // Re-check containment at run time: the stored path was validated, but the folder is chosen now.
            if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(Error(application, "applications.install.path.outsideRoot", "Application path must stay inside the selected folder."));
                continue;
            }

            if (!File.Exists(candidate))
            {
                errors.Add(Error(application, "applications.install.file.missing", $"Installer not found: {application.PackageRelativePath}."));
                continue;
            }

            steps.Add(new ApplicationInstallStep(
                application.Id,
                application.DisplayName,
                candidate,
                application.Arguments,
                application.PackageRelativePath!));
        }

        return new ApplicationInstallPlan(root, steps, manual, errors)
        {
            SkippedByArchitecture = skippedByArchitecture,
        };
    }

    /// <summary>
    /// Finds the installers in a folder the way an operator means it: loose files at the top level,
    /// plus one entry per product subfolder. A subfolder that has its own setup file is treated as a
    /// single product — Office ships hundreds of component .msi files next to its setup.exe, and
    /// listing those individually would be wrong as well as unusable.
    /// </summary>
    public static IReadOnlyList<DiscoveredInstaller> DiscoverInstallers(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return [];
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var found = new List<DiscoveredInstaller>();

        foreach (var file in EnumerateInstallerFiles(root).OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
        {
            found.Add(new DiscoveredInstaller(Path.GetRelativePath(root, file), Path.GetFileNameWithoutExtension(file)));
        }

        foreach (var directory in Directory.EnumerateDirectories(root).OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
        {
            var productName = new DirectoryInfo(directory).Name;
            var setupFile = EnumerateInstallerFiles(directory)
                .FirstOrDefault(static path => IsSetupName(Path.GetFileNameWithoutExtension(path)));
            if (setupFile is not null)
            {
                found.Add(new DiscoveredInstaller(Path.GetRelativePath(root, setupFile), productName));
                continue;
            }

            // No obvious entry point, so every installer directly inside the folder is offered.
            foreach (var file in EnumerateInstallerFiles(directory).OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
            {
                found.Add(new DiscoveredInstaller(
                    Path.GetRelativePath(root, file),
                    $"{productName} — {Path.GetFileNameWithoutExtension(file)}"));
            }
        }

        return found;
    }

    private static IEnumerable<string> EnumerateInstallerFiles(string directory)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory, "*", new EnumerationOptions { IgnoreInaccessible = true });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var file in files)
        {
            var extension = Path.GetExtension(file);
            var isInstaller = extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".msi", StringComparison.OrdinalIgnoreCase);
            if (isInstaller && !IsIgnoredName(Path.GetFileNameWithoutExtension(file)))
            {
                yield return file;
            }
        }
    }

    private static bool IsSetupName(string fileName) =>
        fileName.Equals("setup", StringComparison.OrdinalIgnoreCase)
        || fileName.Equals("install", StringComparison.OrdinalIgnoreCase)
        || fileName.Equals("installer", StringComparison.OrdinalIgnoreCase);

    /// <summary>Uninstallers and helpers must never end up in an install queue.</summary>
    private static bool IsIgnoredName(string fileName) =>
        fileName.StartsWith("unins", StringComparison.OrdinalIgnoreCase)
        || fileName.Contains("uninstall", StringComparison.OrdinalIgnoreCase)
        || fileName.Contains("repair", StringComparison.OrdinalIgnoreCase)
        || fileName.Equals("autorun", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads the architecture of the running Windows, not of this process.</summary>
    public static ApplicationArchitecture GetCurrentSystemArchitecture() =>
        Environment.Is64BitOperatingSystem ? ApplicationArchitecture.X64 : ApplicationArchitecture.X86;

    /// <summary>
    /// Runs the planned installers in profile order and stops at the first failure, so a broken
    /// installer cannot be followed by others that assume it succeeded.
    /// </summary>
    public ApplicationInstallReport Run(ApplicationInstallPlan plan, IApplicationInstallerRunner runner)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(runner);

        var results = new List<ApplicationInstallStepResult>();
        if (!plan.CanRun)
        {
            return new ApplicationInstallReport(results, StoppedOnFailure: false);
        }

        var stopped = false;
        foreach (var step in plan.Steps)
        {
            if (stopped)
            {
                results.Add(new ApplicationInstallStepResult(step, ApplicationInstallOutcome.NotRun, null, null));
                continue;
            }

            var processResult = runner.Run(step);
            var outcome = DescribeOutcome(processResult);
            results.Add(new ApplicationInstallStepResult(step, outcome, processResult.ExitCode, processResult.ErrorMessage));
            if (outcome == ApplicationInstallOutcome.Failed)
            {
                stopped = true;
            }
        }

        return new ApplicationInstallReport(results, stopped);
    }

    private static ApplicationInstallOutcome DescribeOutcome(ApplicationProcessResult result) => result switch
    {
        { ErrorMessage: not null } => ApplicationInstallOutcome.Failed,
        { ExitCode: 0 } => ApplicationInstallOutcome.Installed,
        { ExitCode: ExitCodeRestartRequired or ExitCodeRestartInitiated } => ApplicationInstallOutcome.InstalledRestartRequired,
        _ => ApplicationInstallOutcome.Failed,
    };

    private static DeploymentValidationError Error(ApplicationProfile application, string code, string message) =>
        new(code, $"applications[{application.Id}]", message);
}
