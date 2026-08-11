using Easyaller.Core.Profiles;
using Easyaller.Deployment;

namespace Easyaller.Core.Tests;

public sealed class RuntimeProfileEligibilityServiceTests
{
    [Fact]
    public void Evaluate_DocumentedMatchingWindows_IsReady()
    {
        var service = CreateService(new RuntimeWindowsInfo(
            RuntimeWindowsEdition.Professional,
            RuntimeWindowsArchitecture.Amd64,
            "24H2",
            26100));

        var result = service.Evaluate(CreateProfile(WindowsEdition.Professional));

        Assert.Equal(ProfileEligibilityState.Ready, result.State);
        Assert.True(result.CanApply);
        Assert.Contains("Pro", result.Summary);
    }

    [Fact]
    public void Evaluate_EditionOutsideTheProfile_IsBlocked()
    {
        var service = CreateService(new RuntimeWindowsInfo(
            RuntimeWindowsEdition.Enterprise,
            RuntimeWindowsArchitecture.Amd64,
            "24H2",
            26100));

        var result = service.Evaluate(CreateProfile(WindowsEdition.Professional));

        Assert.Equal(ProfileEligibilityState.Blocked, result.State);
        Assert.False(result.CanApply);
        Assert.Contains("Enterprise", result.Reason);
    }

    [Fact]
    public void Evaluate_NonAmd64_IsBlocked()
    {
        var service = CreateService(new RuntimeWindowsInfo(
            RuntimeWindowsEdition.Professional,
            RuntimeWindowsArchitecture.Other,
            "24H2",
            26100));

        var result = service.Evaluate(CreateProfile(WindowsEdition.Professional));

        Assert.Equal(ProfileEligibilityState.Blocked, result.State);
    }

    [Fact]
    public void Evaluate_UnknownBuild_WarnsButStillAllows()
    {
        var service = CreateService(new RuntimeWindowsInfo(
            RuntimeWindowsEdition.Professional,
            RuntimeWindowsArchitecture.Amd64,
            "23H2",
            22631));

        var result = service.Evaluate(CreateProfile(WindowsEdition.Professional));

        Assert.Equal(ProfileEligibilityState.Warning, result.State);
        Assert.True(result.CanApply);
    }

    [Fact]
    public void Evaluate_UnknownEdition_WarnsInsteadOfGuessing()
    {
        var service = CreateService(new RuntimeWindowsInfo(
            RuntimeWindowsEdition.Unknown,
            RuntimeWindowsArchitecture.Amd64,
            "24H2",
            26100));

        var result = service.Evaluate(CreateProfile(WindowsEdition.Professional));

        Assert.Equal(ProfileEligibilityState.Warning, result.State);
        Assert.True(result.CanApply);
    }

    [Fact]
    public void Evaluate_UndetectableWindows_WarnsRatherThanBlocking()
    {
        var service = new RuntimeProfileEligibilityService(
            new StubRuntimeInfoProvider(new RuntimeWindowsDetectionResult(null, "runtime.windows.platform.unsupported", "Только Windows.")));

        var result = service.Evaluate(CreateProfile(WindowsEdition.Professional));

        Assert.Equal(ProfileEligibilityState.Warning, result.State);
        Assert.Null(result.Runtime);
    }

    private static RuntimeProfileEligibilityService CreateService(RuntimeWindowsInfo runtime) =>
        new(new StubRuntimeInfoProvider(new RuntimeWindowsDetectionResult(runtime, null, null)));

    private static ProvisioningProfile CreateProfile(params WindowsEdition[] supportedEditions)
    {
        var profile = ProvisioningProfileFactory.CreateDefault();
        return profile with { Windows = profile.Windows with { SupportedEditions = supportedEditions } };
    }

    private sealed class StubRuntimeInfoProvider(RuntimeWindowsDetectionResult result) : IRuntimeWindowsInfoProvider
    {
        public RuntimeWindowsDetectionResult Detect() => result;
    }
}
