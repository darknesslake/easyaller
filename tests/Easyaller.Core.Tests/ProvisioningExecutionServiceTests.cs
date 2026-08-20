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
        var adapter = new FakeSystemAdapter { CurrentComputerName = "OLD-NAME" };
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
    public void Execute_SelectedTimeZone_DoesNotValidateOrChangeOtherProfileSettings()
    {
        var adapter = new FakeSystemAdapter();
        var service = CreateService(adapter, out var stateStore, out var launcher);
        using var inputs = new RuntimeProvisioningInputs
        {
            ApplyTimeZone = true,
            SelectedOperations = ProvisioningOperationSelection.TimeZone,
        };

        var result = service.Execute(CreatePlan(), inputs, ProvisioningExecutionService.ConfirmationPhrase);

        Assert.Equal(ProvisioningExecutionStatus.Completed, result.Status);
        Assert.Equal([nameof(FakeSystemAdapter.SetTimeZone)], adapter.Calls);
        Assert.Null(stateStore.Pending);
        Assert.Equal(0, launcher.RegisterCount);
    }

    [Fact]
    public void Execute_WithoutSelectedOperations_IsBlockedBeforeCallingWindowsAdapter()
    {
        var adapter = new FakeSystemAdapter();
        var service = CreateService(adapter, out _, out _);
        using var inputs = new RuntimeProvisioningInputs
        {
            SelectedOperations = ProvisioningOperationSelection.None,
        };

        var result = service.Execute(CreatePlan(), inputs, ProvisioningExecutionService.ConfirmationPhrase);

        Assert.Equal(ProvisioningExecutionStatus.Blocked, result.Status);
        Assert.Contains(result.Errors, error => error.Code == "runtime.operations.required");
        Assert.Empty(adapter.Calls);
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

    [Fact]
    public void Execute_StaticIpv4Profile_ConfiguresOnlyTheVerifiedAdapter()
    {
        var defaultProfile = ProvisioningProfileFactory.CreateDefault();
        var profile = defaultProfile with
        {
            Domain = defaultProfile.Domain with { Mode = DomainMode.NotConfigured },
            Machine = defaultProfile.Machine with
            {
                Network = new NetworkSettings(
                    NetworkConfigurationMode.StaticIpv4,
                    new StaticIpv4Configuration("192.0.2.77", "255.255.255.0", "192.0.2.254", ["192.0.2.53"])),
                Proxy = new ProxySettings(ProxyConfigurationMode.NotConfigured),
            },
        };
        var adapter = new FakeSystemAdapter { CurrentComputerName = "OLD-NAME" };
        var service = CreateService(adapter, out _, out _);
        using var inputs = new RuntimeProvisioningInputs
        {
            ComputerName = "LAB-WS-01",
            NetworkAdapterId = "adapter-1",
        };

        var result = service.Execute(CreatePlan(profile), inputs, ProvisioningExecutionService.ConfirmationPhrase);

        Assert.True(result.IsSuccess);
        Assert.Equal(["VerifyNetworkAdapter", "ConfigureStaticIpv4", "VerifyComputerName", "RenameComputer"], adapter.Calls);
        Assert.Contains(result.Operations, operation => operation.Kind == ProvisioningExecutionOperationKind.ConfigureStaticIpv4 && operation.WasApplied);
        Assert.Equal("adapter-1", adapter.StaticIpv4AdapterId);
    }

    [Fact]
    public void Execute_RuntimeProxyProfile_AppliesTheProfileBypassList()
    {
        var defaultProfile = ProvisioningProfileFactory.CreateDefault();
        var profile = defaultProfile with
        {
            Domain = defaultProfile.Domain with { Mode = DomainMode.NotConfigured },
            Machine = defaultProfile.Machine with
            {
                Proxy = new ProxySettings(ProxyConfigurationMode.PromptAtRuntime, ["*.example.test", "<local>"]),
            },
        };
        var adapter = new FakeSystemAdapter();
        var service = CreateService(adapter, out _, out _);
        using var inputs = new RuntimeProvisioningInputs
        {
            ComputerName = "LAB-WS-01",
            NetworkAdapterId = "adapter-1",
            ProxyAddress = "http://proxy.example.test:8080",
        };

        var result = service.Execute(CreatePlan(profile), inputs, ProvisioningExecutionService.ConfirmationPhrase);

        Assert.True(result.IsSuccess);
        Assert.Contains("SetWinHttpProxy", adapter.Calls);
        Assert.Equal(["*.example.test", "<local>"], adapter.ProxyBypassList);
    }

    [Fact]
    public void ExecuteTimeZone_AppliesOnlyTheSavedTimeZone()
    {
        var adapter = new FakeSystemAdapter();
        var service = CreateService(adapter, out var stateStore, out var launcher);

        var result = service.ExecuteTimeZone(CreatePlan(), ProvisioningExecutionService.ConfirmationPhrase);

        Assert.Equal(ProvisioningExecutionStatus.Completed, result.Status);
        Assert.Equal(["SetTimeZone"], adapter.Calls);
        Assert.Equal("UTC", adapter.TimeZone);
        Assert.Null(stateStore.Pending);
        Assert.Equal(0, launcher.RegisterCount);
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

    [Fact]
    public void Execute_WhenComputerNameAlreadyMatches_SkipsTheRenameAndTheRestart()
    {
        var defaultProfile = ProvisioningProfileFactory.CreateDefault();
        var profile = defaultProfile with
        {
            Domain = defaultProfile.Domain with { Mode = DomainMode.NotConfigured },
            Machine = defaultProfile.Machine with { Proxy = new ProxySettings(ProxyConfigurationMode.NotConfigured) },
        };
        var adapter = new FakeSystemAdapter { CurrentComputerName = "LAB-WS-01" };
        var service = CreateService(adapter, out var stateStore, out var launcher);
        using var inputs = new RuntimeProvisioningInputs
        {
            ComputerName = "LAB-WS-01",
            NetworkAdapterId = "adapter-1",
        };

        var result = service.Execute(CreatePlan(profile), inputs, ProvisioningExecutionService.ConfirmationPhrase);

        Assert.Equal(ProvisioningExecutionStatus.Completed, result.Status);
        Assert.DoesNotContain("RenameComputer", adapter.Calls);
        Assert.Contains(
            result.Operations,
            static operation => operation.Kind == ProvisioningExecutionOperationKind.RenameComputer && !operation.WasApplied);
        Assert.Null(stateStore.Pending);
        Assert.Equal(0, launcher.RegisterCount);
    }

    [Fact]
    public void Execute_WithoutTimeZoneOptIn_LeavesTheClockAlone()
    {
        var adapter = new FakeSystemAdapter();
        var service = CreateService(adapter, out _, out _);
        using var inputs = CreateInputs();

        var result = service.Execute(CreatePlan(), inputs, ProvisioningExecutionService.ConfirmationPhrase);

        Assert.True(result.IsSuccess);
        Assert.DoesNotContain(nameof(FakeSystemAdapter.SetTimeZone), adapter.Calls);
        Assert.Null(adapter.TimeZone);
    }

    [Fact]
    public void Execute_WithTimeZoneOptIn_AppliesItBeforeEverythingElse()
    {
        var adapter = new FakeSystemAdapter();
        var service = CreateService(adapter, out _, out _);
        using var inputs = CreateInputs(applyTimeZone: true);
        var plan = CreatePlan();

        var result = service.Execute(plan, inputs, ProvisioningExecutionService.ConfirmationPhrase);

        Assert.True(result.IsSuccess);
        Assert.Equal(nameof(FakeSystemAdapter.SetTimeZone), adapter.Calls[0]);
        Assert.Equal(plan.TimeZone, adapter.TimeZone);
        Assert.Contains(
            result.Operations,
            static operation => operation.Kind == ProvisioningExecutionOperationKind.SetTimeZone && operation.WasApplied);
    }

    [Fact]
    public void Execute_TimeZoneOptInWithoutPlanValue_BlocksBeforeAnyChange()
    {
        // A valid profile always carries a time zone, so the empty case is built directly
        // to prove the executor still refuses rather than calling Windows with an empty value.
        var plan = CreatePlan() with { TimeZone = string.Empty };
        var adapter = new FakeSystemAdapter();
        var service = CreateService(adapter, out _, out _);
        using var inputs = CreateInputs(applyTimeZone: true);

        var result = service.Execute(plan, inputs, ProvisioningExecutionService.ConfirmationPhrase);

        Assert.Equal(ProvisioningExecutionStatus.Blocked, result.Status);
        Assert.Empty(adapter.Calls);
    }

    private static ProvisioningPlan CreatePlan(ProvisioningProfile? profile = null) =>
        new ProvisioningPlanBuilder().Create(profile ?? ProvisioningProfileFactory.CreateDefault()).Plan!;

    private static RuntimeProvisioningInputs CreateInputs(
        string? domainName = null,
        bool includeCredential = false,
        bool applyTimeZone = false)
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
            ApplyTimeZone = applyTimeZone,
        };
    }

    private sealed class FakeSystemAdapter : IProvisioningSystemAdapter
    {
        public List<string> Calls { get; } = [];

        public ProvisioningSystemOperationResult VerifyNameResult { get; init; } = ProvisioningSystemOperationResult.Success();

        public ProvisioningSystemOperationResult JoinDomainResult { get; init; } = ProvisioningSystemOperationResult.Success(requiresRestart: true);

        public string? TimeZone { get; private set; }

        public ProvisioningSystemOperationResult SetTimeZone(string timeZone)
        {
            Calls.Add(nameof(SetTimeZone));
            TimeZone = timeZone;
            return ProvisioningSystemOperationResult.Success();
        }

        public ProvisioningSystemOperationResult VerifyNetworkAdapter(string adapterId)
        {
            Calls.Add(nameof(VerifyNetworkAdapter));
            return ProvisioningSystemOperationResult.Success();
        }

        public string? StaticIpv4AdapterId { get; private set; }

        public ProvisioningSystemOperationResult ConfigureStaticIpv4(string adapterId, StaticIpv4Configuration configuration)
        {
            Calls.Add(nameof(ConfigureStaticIpv4));
            StaticIpv4AdapterId = adapterId;
            Assert.Equal("192.0.2.77", configuration.Address);
            return ProvisioningSystemOperationResult.Success();
        }

        public IReadOnlyList<string> ProxyBypassList { get; private set; } = [];

        public ProvisioningSystemOperationResult SetWinHttpProxy(string proxyAddress, IReadOnlyList<string> bypassList)
        {
            Calls.Add(nameof(SetWinHttpProxy));
            ProxyBypassList = bypassList;
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

        /// <summary>The name the machine already carries, when a test needs to model that.</summary>
        public string? CurrentComputerName { get; init; }

        public ProvisioningSystemOperationResult VerifyComputerName(string expectedComputerName)
        {
            Calls.Add(nameof(VerifyComputerName));
            return CurrentComputerName is null
                ? VerifyNameResult
                : string.Equals(CurrentComputerName, expectedComputerName, StringComparison.OrdinalIgnoreCase)
                    ? ProvisioningSystemOperationResult.Success()
                    : ProvisioningSystemOperationResult.Failure("execution.resume.computerName.unverified");
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
