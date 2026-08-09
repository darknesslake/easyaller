using Easyaller.Core.Profiles;
using Easyaller.Deployment;

namespace Easyaller.Core.Tests;

public sealed class RuntimeWindowsVersionGateTests
{
    [Fact]
    public void Evaluate_MatchingDocumentedRuntime_AllowsValidatedActions()
    {
        var profile = ProvisioningProfileFactory.CreateDefault();
        var manifest = Manifest(profile, Target());
        var result = new RuntimeWindowsVersionGate().Evaluate(Runtime("24H2", 26100), manifest, profile);

        Assert.Equal(RuntimeVersionGateState.Ready, result.State);
        Assert.True(result.CanRunValidatedActions);
        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Evaluate_ManifestTargetMismatch_BlocksActions()
    {
        var profile = ProvisioningProfileFactory.CreateDefault();
        var manifest = Manifest(profile, Target());
        var result = new RuntimeWindowsVersionGate().Evaluate(Runtime("25H2", 26200), manifest, profile);

        Assert.Equal(RuntimeVersionGateState.Blocked, result.State);
        Assert.False(result.CanRunValidatedActions);
        Assert.Contains(result.Errors, error => error.Code == "runtime.manifest.target.mismatch");
    }

    [Fact]
    public void Evaluate_UnknownBuild_WarnsAndSkipsValidatedActions()
    {
        var profile = ProvisioningProfileFactory.CreateDefault();
        var target = Target(displayVersion: "26H2", build: 27000);
        var manifest = Manifest(profile, target);
        var result = new RuntimeWindowsVersionGate().Evaluate(Runtime("26H2", 27000), manifest, profile);

        Assert.Equal(RuntimeVersionGateState.Warning, result.State);
        Assert.False(result.CanRunValidatedActions);
        Assert.Contains(result.Warnings, warning => warning.Code == "runtime.windows.build.unvalidated");
    }

    [Fact]
    public void Evaluate_ProfileIdentityMismatch_BlocksActions()
    {
        var profile = ProvisioningProfileFactory.CreateDefault();
        var manifest = Manifest(profile, Target()) with { ProfileRevision = profile.Revision + 1 };
        var result = new RuntimeWindowsVersionGate().Evaluate(Runtime("24H2", 26100), manifest, profile);

        Assert.Equal(RuntimeVersionGateState.Blocked, result.State);
        Assert.Contains(result.Errors, error => error.Code == "runtime.package.profile.mismatch");
    }

    [Fact]
    public void DetectAndEvaluate_UnavailableRuntime_WarnsWithoutRunningActions()
    {
        var profile = ProvisioningProfileFactory.CreateDefault();
        var gate = new RuntimeWindowsVersionGate(new UnavailableRuntimeProvider());

        var result = gate.DetectAndEvaluate(Manifest(profile, Target()), profile);

        Assert.Equal(RuntimeVersionGateState.Warning, result.State);
        Assert.False(result.CanRunValidatedActions);
        Assert.Contains(result.Warnings, warning => warning.Code == "runtime.windows.platform.unsupported");
    }

    private static RuntimeWindowsInfo Runtime(string displayVersion, int build) =>
        new(RuntimeWindowsEdition.Professional, RuntimeWindowsArchitecture.Amd64, displayVersion, build);

    private static WindowsDeploymentTarget Target(string displayVersion = "24H2", int build = 26100) =>
        new(WindowsEdition.Professional, WindowsArchitecture.Amd64, displayVersion, build);

    private static DeploymentPackageManifest Manifest(ProvisioningProfile profile, WindowsDeploymentTarget target) =>
        new(1, profile.ProfileId, profile.Revision, target, DeploymentCompatibilityState.Documented, []);

    private sealed class UnavailableRuntimeProvider : IRuntimeWindowsInfoProvider
    {
        public RuntimeWindowsDetectionResult Detect() =>
            new(null, "runtime.windows.platform.unsupported", "Windows runtime detection is available only on Windows.");
    }
}
