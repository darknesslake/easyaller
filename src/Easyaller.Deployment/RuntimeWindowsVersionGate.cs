using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Easyaller.Core.Profiles;
using Microsoft.Win32;

namespace Easyaller.Deployment;

public enum RuntimeWindowsEdition
{
    Professional,
    Enterprise,
    Unknown,
}

public enum RuntimeWindowsArchitecture
{
    Amd64,
    Other,
}

public sealed record RuntimeWindowsInfo(
    RuntimeWindowsEdition Edition,
    RuntimeWindowsArchitecture Architecture,
    string DisplayVersion,
    int Build);

public sealed record RuntimeWindowsDetectionResult(
    RuntimeWindowsInfo? Runtime,
    string? ErrorCode,
    string? ErrorMessage)
{
    public bool IsDetected => Runtime is not null && ErrorCode is null;
}

public sealed record RuntimeVersionGateMessage(string Code, string Message);

public enum RuntimeVersionGateState
{
    Ready,
    Warning,
    Blocked,
}

public sealed record RuntimeVersionGateResult(
    RuntimeVersionGateState State,
    RuntimeWindowsInfo? Runtime,
    IReadOnlyList<RuntimeVersionGateMessage> Errors,
    IReadOnlyList<RuntimeVersionGateMessage> Warnings)
{
    public bool CanRunValidatedActions => State == RuntimeVersionGateState.Ready && Errors.Count == 0;
}

public interface IRuntimeWindowsInfoProvider
{
    RuntimeWindowsDetectionResult Detect();
}

[SupportedOSPlatform("windows")]
public sealed class WindowsRuntimeInfoProvider : IRuntimeWindowsInfoProvider
{
    private const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    public RuntimeWindowsDetectionResult Detect()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Unavailable("runtime.windows.platform.unsupported", "Windows runtime detection is available only on Windows.");
        }

        using var key = Registry.LocalMachine.OpenSubKey(CurrentVersionKey, writable: false);
        var edition = GetEdition(key?.GetValue("EditionID") as string);
        var displayVersion = key?.GetValue("DisplayVersion") as string;
        var buildText = key?.GetValue("CurrentBuildNumber") as string;
        if (string.IsNullOrWhiteSpace(displayVersion) || !int.TryParse(buildText, out var build) || build < 1)
        {
            return Unavailable("runtime.windows.version.unavailable", "Windows display version or build could not be read.");
        }

        var architecture = RuntimeInformation.OSArchitecture == Architecture.X64
            ? RuntimeWindowsArchitecture.Amd64
            : RuntimeWindowsArchitecture.Other;
        return new RuntimeWindowsDetectionResult(
            new RuntimeWindowsInfo(edition, architecture, displayVersion, build),
            null,
            null);
    }

    private static RuntimeWindowsEdition GetEdition(string? editionId) => editionId switch
    {
        "Professional" => RuntimeWindowsEdition.Professional,
        "Enterprise" => RuntimeWindowsEdition.Enterprise,
        _ => RuntimeWindowsEdition.Unknown,
    };

    private static RuntimeWindowsDetectionResult Unavailable(string code, string message) =>
        new(null, code, message);
}

public interface IRuntimeWindowsVersionGate
{
    RuntimeVersionGateResult Evaluate(
        RuntimeWindowsInfo runtime,
        DeploymentPackageManifest manifest,
        ProvisioningProfile profile);

    RuntimeVersionGateResult DetectAndEvaluate(
        DeploymentPackageManifest manifest,
        ProvisioningProfile profile);
}

public sealed class RuntimeWindowsVersionGate(
    IRuntimeWindowsInfoProvider? runtimeInfoProvider = null,
    IWindowsCompatibilityCatalog? compatibilityCatalog = null) : IRuntimeWindowsVersionGate
{
    private readonly IRuntimeWindowsInfoProvider _runtimeInfoProvider = runtimeInfoProvider ?? CreateDefaultRuntimeInfoProvider();
    private readonly IWindowsCompatibilityCatalog _compatibilityCatalog = compatibilityCatalog ?? new Windows11CompatibilityCatalog();

    public RuntimeVersionGateResult DetectAndEvaluate(
        DeploymentPackageManifest manifest,
        ProvisioningProfile profile)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(profile);
        var detection = _runtimeInfoProvider.Detect();
        return detection.IsDetected
            ? Evaluate(detection.Runtime!, manifest, profile)
            : new RuntimeVersionGateResult(
                RuntimeVersionGateState.Warning,
                null,
                [],
                [new RuntimeVersionGateMessage(detection.ErrorCode!, detection.ErrorMessage!)]);
    }

    public RuntimeVersionGateResult Evaluate(
        RuntimeWindowsInfo runtime,
        DeploymentPackageManifest manifest,
        ProvisioningProfile profile)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(profile);

        var errors = new List<RuntimeVersionGateMessage>();
        var warnings = new List<RuntimeVersionGateMessage>();
        if (manifest.ProfileId != profile.ProfileId || manifest.ProfileRevision != profile.Revision)
        {
            errors.Add(new RuntimeVersionGateMessage(
                "runtime.package.profile.mismatch",
                "Deployment manifest does not match the selected profile identity or revision."));
        }

        if (!IsProfileCompatibleWithManifest(profile, manifest.Target))
        {
            errors.Add(new RuntimeVersionGateMessage(
                "runtime.package.profile.target.mismatch",
                "Selected profile does not support the deployment target recorded in the manifest."));
        }

        if (!MatchesManifestTarget(runtime, manifest.Target))
        {
            errors.Add(new RuntimeVersionGateMessage(
                "runtime.manifest.target.mismatch",
                "Installed Windows edition, architecture, display version, or build does not match the deployment manifest."));
        }

        if (errors.Count > 0)
        {
            return new RuntimeVersionGateResult(RuntimeVersionGateState.Blocked, runtime, errors, warnings);
        }

        var catalogEntry = _compatibilityCatalog.Find(manifest.Target);
        if (catalogEntry is null || !catalogEntry.BuildRange.Contains(runtime.Build))
        {
            warnings.Add(new RuntimeVersionGateMessage(
                "runtime.windows.build.unvalidated",
                "Installed Windows build is not in the documented compatibility catalog. Validated actions are skipped."));
            return new RuntimeVersionGateResult(RuntimeVersionGateState.Warning, runtime, [], warnings);
        }

        return new RuntimeVersionGateResult(RuntimeVersionGateState.Ready, runtime, [], []);
    }

    private static bool IsProfileCompatibleWithManifest(ProvisioningProfile profile, WindowsDeploymentTarget target) =>
        profile.Windows.Architecture == target.Architecture &&
        profile.Windows.SupportedEditions.Contains(target.Edition);

    private static bool MatchesManifestTarget(RuntimeWindowsInfo runtime, WindowsDeploymentTarget target) =>
        TryMapEdition(runtime.Edition, out var edition) &&
        runtime.Architecture == RuntimeWindowsArchitecture.Amd64 &&
        target.Architecture == WindowsArchitecture.Amd64 &&
        edition == target.Edition &&
        string.Equals(runtime.DisplayVersion, target.DisplayVersion, StringComparison.OrdinalIgnoreCase) &&
        runtime.Build == target.Build;

    private static bool TryMapEdition(RuntimeWindowsEdition runtimeEdition, out WindowsEdition edition)
    {
        switch (runtimeEdition)
        {
            case RuntimeWindowsEdition.Professional:
                edition = WindowsEdition.Professional;
                return true;
            case RuntimeWindowsEdition.Enterprise:
                edition = WindowsEdition.Enterprise;
                return true;
            default:
                edition = default;
                return false;
        }
    }

    /// <summary>
    /// Creates the detector for the current platform. Shared so every runtime check reads Windows
    /// the same way instead of each caller reimplementing the platform guard.
    /// </summary>
    public static IRuntimeWindowsInfoProvider CreateDefaultRuntimeInfoProvider() =>
        OperatingSystem.IsWindows()
            ? CreateWindowsRuntimeInfoProvider()
            : new UnsupportedRuntimeInfoProvider();

    [SupportedOSPlatform("windows")]
    private static IRuntimeWindowsInfoProvider CreateWindowsRuntimeInfoProvider() => new WindowsRuntimeInfoProvider();

    private sealed class UnsupportedRuntimeInfoProvider : IRuntimeWindowsInfoProvider
    {
        public RuntimeWindowsDetectionResult Detect() =>
            new(null, "runtime.windows.platform.unsupported", "Windows runtime detection is available only on Windows.");
    }
}
