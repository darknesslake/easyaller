using Easyaller.Core.Profiles;
using Easyaller.Core.Provisioning;

namespace Easyaller.Core.Tests;

public sealed class ProvisioningExecutionServiceTests
{
    [Fact]
    public void Execute_RequiresExplicitConfirmationBeforeCallingWindowsAdapter()
    {
        var adapter = new FakeSystemAdapter();
        var service = CreateService(adapter, out _, out _);
        var plan = CreatePlan();
        using var inputs = CreateInputs();

        var result = service.Execute(plan, inputs, "apply");

        Assert.Equal(ProvisioningExecutionStatus.Blocked, result.Status);
        Assert.Contains(result.Errors, error => error.Code == "execution.confirmation.required");
        Assert.Empty(adapter.Calls);
    }

    [Fact]
    public void Execute_NotConfiguredValuesDoNotCallTheirWindowsOperations()
    {
        var defaultProfile = ProvisioningProfileFactory.CreateDefault();
        var profile = defaultProfile with
        {
            Domain = defaultProfile.Domain with { Mode = DomainMode.NotConfigured },
            Machine = defaultProfile.Machine with { Proxy = new ProxySettings(ProxyConfigurationMode.NotConfigured) },
        };
        var adapter = new FakeSystemAdapter();
        var service = CreateService(adapter, out var stateStore, out var launcher);
        using var inputs = new RuntimeProvisioningInputs
        {
            ComputerName = "LAB-WS-01",
            NetworkAdapterId = "adapter-1",
        };

        var result = service.Execute(CreatePlan(profile), inputs, ProvisioningExecutionService.ConfirmationPhrase);

        Assert.Equal(ProvisioningExecutionStatus.RestartRequired, result.Status);
        Assert.Contains("VerifyNetworkAdapter", adapter.Calls);
        Assert.Contains("RenameComputer", adapter.Calls);
        Assert.DoesNotContain("SetWinHttpProxy", adapter.Calls);
        Assert.DoesNotContain("JoinDomain", adapter.Calls);
        Assert.NotNull(stateStore.Pending);
        Assert.Equal(1, launcher.RegisterCount);
    }

    [Fact]
    public void Execute_DomainCredentialStaysRuntimeOnlyAndSchedulesResume()
    {
        var adapter = new FakeSystemAdapter();
        var service = CreateService(adapter, out var stateStore, out var launcher);
        using var inputs = CreateInputs(domainName: "example.test", includeCredential: true);

        var result = service.Execute(CreatePlan(), inputs, ProvisioningExecutionService.ConfirmationPhrase);

        Assert.Equal(ProvisioningExecutionStatus.RestartRequired, result.Status);
        Assert.Contains("JoinDomain", adapter.Calls);
        Assert.NotNull(stateStore.Pending);
        Assert.True(stateStore.Pending!.DomainJoinRequested);
        Assert.DoesNotContain("example.test", stateStore.SerializedValues);
        Assert.DoesNotContain("temporary-password", stateStore.SerializedValues);
        Assert.Equal(1, launcher.RegisterCount);
        Assert.Equal("Runtime provisioning inputs are redacted.", inputs.ToString());
    }

    [Fact]
    public void Resume_VerifiesPostRestartStateBeforeClearingPendingExecution()
    {
        var adapter = new FakeSystemAdapter();
        var service = CreateService(adapter, out var stateStore, out _);
        using var inputs = CreateInputs(domainName: "example.test", includeCredential: true);
        var execution = service.Execute(CreatePlan(), inputs, ProvisioningExecutionService.ConfirmationPhrase);

        var resumed = service.Resume();

        Assert.Equal(ProvisioningExecutionStatus.Resumed, resumed.Status);
        Assert.Equal(execution.ExecutionId, resumed.ExecutionId);
        Assert.Null(stateStore.Pending);
        Assert.Contains("VerifyComputerName", adapter.Calls);
        Assert.Contains("VerifyDomainJoin", adapter.Calls);
    }

    [Fact]
    public void Resume_DoesNotClearPendingStateWhenVerificationFails()
    {
        var adapter = new FakeSystemAdapter { VerifyNameResult = ProvisioningSystemOperationResult.Failure("execution.resume.computerName.unverified") };
        var service = CreateService(adapter, out var stateStore, out _);
        using var inputs = CreateInputs();
        _ = service.Execute(CreatePlan(), inputs, ProvisioningExecutionService.ConfirmationPhrase);

        var result = service.Resume();

        Assert.Equal(ProvisioningExecutionStatus.Failed, result.Status);
        Assert.NotNull(stateStore.Pending);
    }

    [Fact]
    public void Execute_DomainFailureAfterRename_PreservesResumeForTheCompletedRename()
    {
        var adapter = new FakeSystemAdapter { JoinDomainResult = ProvisioningSystemOperationResult.Failure("execution.domain.failed") };
        var service = CreateService(adapter, out var stateStore, out var launcher);
        using var inputs = CreateInputs(domainName: "example.test", includeCredential: true);

        var result = service.Execute(CreatePlan(), inputs, ProvisioningExecutionService.ConfirmationPhrase);

        Assert.Equal(ProvisioningExecutionStatus.Failed, result.Status);
        Assert.NotNull(result.ExecutionId);
        Assert.NotNull(stateStore.Pending);
        Assert.False(stateStore.Pending!.DomainJoinRequested);
        Assert.Equal(1, launcher.RegisterCount);
        Assert.Contains(result.Warnings, warning => warning.Code == "execution.restart.required.afterPartialFailure");
    }

    private static ProvisioningExecutionService CreateService(
        FakeSystemAdapter adapter,
        out InMemoryStateStore stateStore,
        out FakeResumeLauncher launcher)
    {
        stateStore = new InMemoryStateStore();
        launcher = new FakeResumeLauncher();
        return new ProvisioningExecutionService(adapter, stateStore, launcher);
    }

    private static ProvisioningPlan CreatePlan(ProvisioningProfile? profile = null) =>
        new ProvisioningPlanBuilder().Create(profile ?? ProvisioningProfileFactory.CreateDefault()).Plan!;

    private static RuntimeProvisioningInputs CreateInputs(string? domainName = null, bool includeCredential = false)
    {
        RuntimeDomainCredential? credential = includeCredential
            ? new RuntimeDomainCredential("EXAMPLE\\JoinUser", "temporary-password".AsSpan())
            : null;
        return new RuntimeProvisioningInputs
        {
            ComputerName = "LAB-WS-01",
            NetworkAdapterId = "adapter-1",
            DomainName = domainName,
            DomainCredential = credential,
        };
    }

    private sealed class FakeSystemAdapter : IProvisioningSystemAdapter
    {
        public List<string> Calls { get; } = [];

        public ProvisioningSystemOperationResult VerifyNameResult { get; init; } = ProvisioningSystemOperationResult.Success();

        public ProvisioningSystemOperationResult JoinDomainResult { get; init; } = ProvisioningSystemOperationResult.Success(requiresRestart: true);

        public ProvisioningSystemOperationResult VerifyNetworkAdapter(string adapterId)
        {
            Calls.Add(nameof(VerifyNetworkAdapter));
            return ProvisioningSystemOperationResult.Success();
        }

        public ProvisioningSystemOperationResult SetWinHttpProxy(string proxyAddress)
        {
            Calls.Add(nameof(SetWinHttpProxy));
            return ProvisioningSystemOperationResult.Success();
        }

        public ProvisioningSystemOperationResult RenameComputer(string computerName)
        {
            Calls.Add(nameof(RenameComputer));
            return ProvisioningSystemOperationResult.Success(requiresRestart: true);
        }

        public ProvisioningSystemOperationResult JoinDomain(string domainName, RuntimeDomainCredential credential)
        {
            Calls.Add(nameof(JoinDomain));
            Assert.False(credential.IsDisposed);
            return JoinDomainResult;
        }

        public ProvisioningSystemOperationResult VerifyComputerName(string expectedComputerName)
        {
            Calls.Add(nameof(VerifyComputerName));
            return VerifyNameResult;
        }

        public ProvisioningSystemOperationResult VerifyDomainJoin()
        {
            Calls.Add(nameof(VerifyDomainJoin));
            return ProvisioningSystemOperationResult.Success();
        }
    }

    private sealed class InMemoryStateStore : IProvisioningExecutionStateStore
    {
        public PendingProvisioningExecution? Pending { get; private set; }

        public string SerializedValues => Pending is null
            ? string.Empty
            : string.Join("|", Pending.ProfileId, Pending.ProfileRevision, Pending.ExpectedComputerName, Pending.DomainJoinRequested);

        public PendingProvisioningExecution? ReadPending() => Pending;

        public void SavePending(PendingProvisioningExecution pending) => Pending = pending;

        public void ClearPending(Guid executionId)
        {
            if (Pending?.ExecutionId == executionId)
            {
                Pending = null;
            }
        }
    }

    private sealed class FakeResumeLauncher : IProvisioningResumeLauncher
    {
        public int RegisterCount { get; private set; }

        public ProvisioningSystemOperationResult RegisterResume()
        {
            RegisterCount++;
            return ProvisioningSystemOperationResult.Success();
        }
    }
}
