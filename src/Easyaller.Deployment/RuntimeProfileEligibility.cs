using Easyaller.Core.Profiles;

namespace Easyaller.Deployment;

public enum ProfileEligibilityState
{
    /// <summary>The running Windows matches what the profile supports.</summary>
    Ready,

    /// <summary>Something could not be confirmed; the operator decides whether to continue.</summary>
    Warning,

    /// <summary>The profile must not be applied to this machine.</summary>
    Blocked,
}

public sealed record ProfileEligibilityResult(
    ProfileEligibilityState State,
    RuntimeWindowsInfo? Runtime,
    string Summary,
    string Reason)
{
    public bool CanApply => State != ProfileEligibilityState.Blocked;
}

/// <summary>
/// Answers whether a profile may be applied to the Windows that is running right now.
/// <see cref="RuntimeWindowsVersionGate"/> covers a machine installed from an Easyaller package and
/// needs its manifest; this check works for any running workstation, where no manifest exists.
/// It reads only and changes nothing.
/// </summary>
public sealed class RuntimeProfileEligibilityService(
    IRuntimeWindowsInfoProvider? runtimeInfoProvider = null,
    IWindowsCompatibilityCatalog? compatibilityCatalog = null)
{
    private readonly IRuntimeWindowsInfoProvider _runtimeInfoProvider =
        runtimeInfoProvider ?? RuntimeWindowsVersionGate.CreateDefaultRuntimeInfoProvider();
    private readonly IWindowsCompatibilityCatalog _compatibilityCatalog =
        compatibilityCatalog ?? new Windows11CompatibilityCatalog();

    public ProfileEligibilityResult Evaluate(ProvisioningProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var detection = _runtimeInfoProvider.Detect();
        if (!detection.IsDetected || detection.Runtime is not { } runtime)
        {
            return new ProfileEligibilityResult(
                ProfileEligibilityState.Warning,
                null,
                "Версию Windows определить не удалось",
                detection.ErrorMessage ?? "Проверка совместимости пропущена.");
        }

        var summary = Describe(runtime);

        if (runtime.Architecture != RuntimeWindowsArchitecture.Amd64)
        {
            return new ProfileEligibilityResult(
                ProfileEligibilityState.Blocked,
                runtime,
                summary,
                "Поддерживается только архитектура amd64.");
        }

        if (runtime.Edition == RuntimeWindowsEdition.Unknown)
        {
            return new ProfileEligibilityResult(
                ProfileEligibilityState.Warning,
                runtime,
                summary,
                "Редакция Windows не распознана, поэтому совместимость с профилем не проверена.");
        }

        var runtimeEdition = runtime.Edition == RuntimeWindowsEdition.Professional
            ? WindowsEdition.Professional
            : WindowsEdition.Enterprise;
        if (profile.Windows.SupportedEditions.Count > 0 && !profile.Windows.SupportedEditions.Contains(runtimeEdition))
        {
            var supported = string.Join(", ", profile.Windows.SupportedEditions.Select(DescribeEdition));
            return new ProfileEligibilityResult(
                ProfileEligibilityState.Blocked,
                runtime,
                summary,
                $"Профиль рассчитан на {supported}, а на этом компьютере {DescribeEdition(runtimeEdition)}.");
        }

        var target = new WindowsDeploymentTarget(
            runtimeEdition,
            WindowsArchitecture.Amd64,
            runtime.DisplayVersion,
            runtime.Build);
        if (_compatibilityCatalog.Find(target) is null)
        {
            return new ProfileEligibilityResult(
                ProfileEligibilityState.Warning,
                runtime,
                summary,
                "Эта сборка Windows отсутствует в проверенном каталоге совместимости.");
        }

        return new ProfileEligibilityResult(
            ProfileEligibilityState.Ready,
            runtime,
            summary,
            "Эта Windows входит в проверенный каталог совместимости профиля.");
    }

    private static string Describe(RuntimeWindowsInfo runtime) =>
        $"Windows 11 {DescribeRuntimeEdition(runtime.Edition)}, {runtime.DisplayVersion}, сборка {runtime.Build}";

    private static string DescribeRuntimeEdition(RuntimeWindowsEdition edition) => edition switch
    {
        RuntimeWindowsEdition.Professional => "Pro",
        RuntimeWindowsEdition.Enterprise => "Enterprise",
        _ => "редакция неизвестна",
    };

    private static string DescribeEdition(WindowsEdition edition) =>
        edition == WindowsEdition.Professional ? "Windows 11 Pro" : "Windows 11 Enterprise";
}
