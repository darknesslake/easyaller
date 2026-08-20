using Easyaller.Core.Profiles;

namespace Easyaller.Core.Provisioning;

public enum ProvisioningExecutionStatus
{
    Blocked,
    Failed,
    Completed,
    RestartRequired,
    Resumed,
}

public enum ProvisioningExecutionOperationKind
{
    SetTimeZone,
    VerifyNetworkAdapter,
    ConfigureStaticIpv4,
    SetWinHttpProxy,
    RenameComputer,
    JoinDomain,
}

public sealed record ProvisioningExecutionMessage(string Code, string Message, string? Detail = null);

public sealed record ProvisioningExecutionOperation(
    ProvisioningExecutionOperationKind Kind,
    bool WasApplied,
    bool RequiresRestart);

public sealed record PendingProvisioningExecution(
    Guid ExecutionId,
    Guid ProfileId,
    int ProfileRevision,
    string? ExpectedComputerName,
    bool VerifyComputerNameAfterRestart,
    bool DomainJoinRequested,
    DateTimeOffset CreatedUtc);

public sealed record ProvisioningExecutionResult(
    ProvisioningExecutionStatus Status,
    Guid? ExecutionId,
    IReadOnlyList<ProvisioningExecutionOperation> Operations,
    IReadOnlyList<ProvisioningExecutionMessage> Errors,
    IReadOnlyList<ProvisioningExecutionMessage> Warnings)
{
    public bool IsSuccess => Status is ProvisioningExecutionStatus.Completed or ProvisioningExecutionStatus.RestartRequired or ProvisioningExecutionStatus.Resumed;
}

public sealed record ProvisioningSystemOperationResult(
    bool IsSuccess,
    bool RequiresRestart,
    string? ErrorCode = null,
    /// <summary>
    /// Raw diagnostic text (exit code, stderr) for the operator, kept separate from
    /// <see cref="ErrorCode"/> because the code drives translated UI text while this is
    /// system-generated and shown as-is.
    /// </summary>
    string? Detail = null)
{
    public static ProvisioningSystemOperationResult Success(bool requiresRestart = false) => new(true, requiresRestart);

    public static ProvisioningSystemOperationResult Failure(string errorCode, string? detail = null) => new(false, false, errorCode, detail);
}

public interface IProvisioningSystemAdapter
{
    ProvisioningSystemOperationResult SetTimeZone(string timeZone);

    ProvisioningSystemOperationResult VerifyNetworkAdapter(string adapterId);

    ProvisioningSystemOperationResult ConfigureStaticIpv4(string adapterId, StaticIpv4Configuration configuration);

    ProvisioningSystemOperationResult SetWinHttpProxy(string proxyAddress, IReadOnlyList<string> bypassList);

    ProvisioningSystemOperationResult RenameComputer(string computerName);

    ProvisioningSystemOperationResult JoinDomain(string domainName, RuntimeDomainCredential credential);

    ProvisioningSystemOperationResult VerifyComputerName(string expectedComputerName);

    ProvisioningSystemOperationResult VerifyDomainJoin();
}

public interface IProvisioningExecutionStateStore
{
    PendingProvisioningExecution? ReadPending();

    void SavePending(PendingProvisioningExecution pending);

    void ClearPending(Guid executionId);
}

public interface IProvisioningResumeLauncher
{
    ProvisioningSystemOperationResult RegisterResume();
}

public sealed class ProvisioningExecutionService(
    IProvisioningSystemAdapter systemAdapter,
    IProvisioningExecutionStateStore stateStore,
    IProvisioningResumeLauncher resumeLauncher,
    RuntimeProvisioningInputValidator? inputValidator = null)
{
    public const string ConfirmationPhrase = "APPLY";

    private readonly IProvisioningSystemAdapter _systemAdapter = systemAdapter ?? throw new ArgumentNullException(nameof(systemAdapter));
    private readonly IProvisioningExecutionStateStore _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
    private readonly IProvisioningResumeLauncher _resumeLauncher = resumeLauncher ?? throw new ArgumentNullException(nameof(resumeLauncher));
    private readonly RuntimeProvisioningInputValidator _inputValidator = inputValidator ?? new RuntimeProvisioningInputValidator();

    public ProvisioningExecutionResult Execute(
        ProvisioningPlan plan,
        RuntimeProvisioningInputs inputs,
        string? confirmationPhrase)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(inputs);

        if (!string.Equals(confirmationPhrase, ConfirmationPhrase, StringComparison.Ordinal))
        {
            return Blocked("execution.confirmation.required", $"Type {ConfirmationPhrase} exactly before applying Windows changes.");
        }

        var validation = _inputValidator.Validate(plan, inputs);
        if (!validation.IsValid)
        {
            return new ProvisioningExecutionResult(
                ProvisioningExecutionStatus.Blocked,
                null,
                [],
                validation.Errors.Select(static error => new ProvisioningExecutionMessage(error.Code, error.Message)).ToArray(),
                []);
        }

        var operations = new List<ProvisioningExecutionOperation>();
        var warnings = new List<ProvisioningExecutionMessage>();
        var requiresRestart = false;

        var selected = inputs.SelectedOperations;

        // The time zone runs first: it is the least disruptive step and needs no restart,
        // so a later failure never leaves it half-applied.
        if (selected.HasFlag(ProvisioningOperationSelection.TimeZone) && inputs.ApplyTimeZone)
        {
            if (string.IsNullOrWhiteSpace(plan.TimeZone))
            {
                return Blocked("execution.timeZone.notConfigured", "This profile does not contain a Windows time zone.");
            }

            var timeZoneResult = _systemAdapter.SetTimeZone(plan.TimeZone);
            if (!timeZoneResult.IsSuccess)
            {
                return Failed(operations, timeZoneResult, "execution.timeZone.failed");
            }

            operations.Add(new ProvisioningExecutionOperation(
                ProvisioningExecutionOperationKind.SetTimeZone,
                true,
                timeZoneResult.RequiresRestart));
            requiresRestart |= timeZoneResult.RequiresRestart;
        }

        if (selected.HasFlag(ProvisioningOperationSelection.Network) && HasPrompt(plan, RuntimePromptKind.NetworkConfiguration))
        {
            var networkResult = _systemAdapter.VerifyNetworkAdapter(inputs.NetworkAdapterId!);
            if (!networkResult.IsSuccess)
            {
                return Failed(operations, networkResult, "execution.network.adapter.invalid");
            }

            operations.Add(new ProvisioningExecutionOperation(ProvisioningExecutionOperationKind.VerifyNetworkAdapter, false, false));
        }

        if (selected.HasFlag(ProvisioningOperationSelection.Network) && plan.StaticIpv4 is not null)
        {
            var networkResult = _systemAdapter.ConfigureStaticIpv4(inputs.NetworkAdapterId!, plan.StaticIpv4);
            if (!networkResult.IsSuccess)
            {
                return Failed(operations, networkResult, "execution.network.staticIpv4.failed");
            }

            operations.Add(new ProvisioningExecutionOperation(
                ProvisioningExecutionOperationKind.ConfigureStaticIpv4,
                true,
                networkResult.RequiresRestart));
            requiresRestart |= networkResult.RequiresRestart;
        }

        if (selected.HasFlag(ProvisioningOperationSelection.Proxy) && HasPrompt(plan, RuntimePromptKind.ProxyConfiguration))
        {
            var proxyResult = _systemAdapter.SetWinHttpProxy(inputs.ProxyAddress!, plan.ProxyBypassList ?? []);
            if (!proxyResult.IsSuccess)
            {
                return Failed(operations, proxyResult, "execution.proxy.failed");
            }

            operations.Add(new ProvisioningExecutionOperation(
                ProvisioningExecutionOperationKind.SetWinHttpProxy,
                true,
                proxyResult.RequiresRestart));
            requiresRestart |= proxyResult.RequiresRestart;
        }

        var computerNameSelected = selected.HasFlag(ProvisioningOperationSelection.ComputerName);
        // Renaming always costs a restart, so a machine that already carries the expected name
        // is left alone and the operation is reported as verified rather than applied.
        if (computerNameSelected && _systemAdapter.VerifyComputerName(inputs.ComputerName!).IsSuccess)
        {
            operations.Add(new ProvisioningExecutionOperation(
                ProvisioningExecutionOperationKind.RenameComputer,
                false,
                false));
        }
        else if (computerNameSelected)
        {
            var renameResult = _systemAdapter.RenameComputer(inputs.ComputerName!);
            if (!renameResult.IsSuccess)
            {
                return Failed(operations, renameResult, "execution.computerName.failed");
            }

            operations.Add(new ProvisioningExecutionOperation(
                ProvisioningExecutionOperationKind.RenameComputer,
                true,
                renameResult.RequiresRestart));
            requiresRestart |= renameResult.RequiresRestart;
        }

        var domainJoinRequested = selected.HasFlag(ProvisioningOperationSelection.Domain)
            && !string.IsNullOrWhiteSpace(inputs.DomainName);
        if (domainJoinRequested)
        {
            var domainResult = _systemAdapter.JoinDomain(inputs.DomainName!, inputs.DomainCredential!);
            if (!domainResult.IsSuccess)
            {
                return FailedAfterRestartRequired(plan, inputs, operations, domainResult, "execution.domain.failed");
            }

            operations.Add(new ProvisioningExecutionOperation(
                ProvisioningExecutionOperationKind.JoinDomain,
                true,
                domainResult.RequiresRestart));
            requiresRestart |= domainResult.RequiresRestart;
        }

        if (!requiresRestart)
        {
            return new ProvisioningExecutionResult(ProvisioningExecutionStatus.Completed, null, operations, [], warnings);
        }

        var pending = CreatePending(plan, inputs, computerNameSelected, domainJoinRequested);
        _stateStore.SavePending(pending);

        var resumeResult = _resumeLauncher.RegisterResume();
        if (!resumeResult.IsSuccess)
        {
            warnings.Add(new ProvisioningExecutionMessage(
                resumeResult.ErrorCode ?? "execution.resume.register.failed",
                "Windows changes are pending a restart. Reopen Easyaller with --resume-provisioning after the restart."));
        }

        return new ProvisioningExecutionResult(
            ProvisioningExecutionStatus.RestartRequired,
            pending.ExecutionId,
            operations,
            [],
            warnings);
    }

    public ProvisioningExecutionResult ExecuteTimeZone(ProvisioningPlan plan, string? confirmationPhrase)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (!string.Equals(confirmationPhrase, ConfirmationPhrase, StringComparison.Ordinal))
        {
            return Blocked("execution.confirmation.required", $"Type {ConfirmationPhrase} exactly before applying Windows changes.");
        }

        if (string.IsNullOrWhiteSpace(plan.TimeZone))
        {
            return Blocked("execution.timeZone.notConfigured", "This profile does not contain a Windows time zone.");
        }

        var result = _systemAdapter.SetTimeZone(plan.TimeZone);
        if (!result.IsSuccess)
        {
            return Failed([], result, "execution.timeZone.failed");
        }

        return new ProvisioningExecutionResult(
            result.RequiresRestart ? ProvisioningExecutionStatus.RestartRequired : ProvisioningExecutionStatus.Completed,
            null,
            [new ProvisioningExecutionOperation(ProvisioningExecutionOperationKind.SetTimeZone, true, result.RequiresRestart)],
            [],
            []);
    }

    public ProvisioningExecutionResult Resume()
    {
        var pending = _stateStore.ReadPending();
        if (pending is null)
        {
            return Blocked("execution.resume.notFound", "No pending Easyaller provisioning execution was found.");
        }

        var operations = new List<ProvisioningExecutionOperation>();
        // Older resume records did not contain the boolean flag. Their non-empty name is
        // sufficient to preserve the original verification behavior after an upgrade.
        if (pending.VerifyComputerNameAfterRestart || !string.IsNullOrWhiteSpace(pending.ExpectedComputerName))
        {
            var nameResult = _systemAdapter.VerifyComputerName(pending.ExpectedComputerName!);
            if (!nameResult.IsSuccess)
            {
                return Failed(operations, nameResult, "execution.resume.computerName.unverified", pending.ExecutionId);
            }

            operations.Add(new ProvisioningExecutionOperation(ProvisioningExecutionOperationKind.RenameComputer, false, false));
        }
        if (pending.DomainJoinRequested)
        {
            var domainResult = _systemAdapter.VerifyDomainJoin();
            if (!domainResult.IsSuccess)
            {
                return Failed(operations, domainResult, "execution.resume.domain.unverified", pending.ExecutionId);
            }

            operations.Add(new ProvisioningExecutionOperation(ProvisioningExecutionOperationKind.JoinDomain, false, false));
        }

        _stateStore.ClearPending(pending.ExecutionId);
        return new ProvisioningExecutionResult(
            ProvisioningExecutionStatus.Resumed,
            pending.ExecutionId,
            operations,
            [],
            []);
    }

    private static bool HasPrompt(ProvisioningPlan plan, RuntimePromptKind kind) =>
        plan.RuntimePrompts.Any(prompt => prompt.Kind == kind);

    private static ProvisioningExecutionResult Blocked(string code, string message) =>
        new(ProvisioningExecutionStatus.Blocked, null, [], [new ProvisioningExecutionMessage(code, message)], []);

    private static ProvisioningExecutionResult Failed(
        IReadOnlyList<ProvisioningExecutionOperation> operations,
        ProvisioningSystemOperationResult result,
        string fallbackCode,
        Guid? executionId = null) =>
        new(
            ProvisioningExecutionStatus.Failed,
            executionId,
            operations,
            [new ProvisioningExecutionMessage(result.ErrorCode ?? fallbackCode, "A Windows provisioning operation did not complete.", result.Detail)],
            []);

    private ProvisioningExecutionResult FailedAfterRestartRequired(
        ProvisioningPlan plan,
        RuntimeProvisioningInputs inputs,
        IReadOnlyList<ProvisioningExecutionOperation> operations,
        ProvisioningSystemOperationResult result,
        string fallbackCode)
    {
        var pending = CreatePending(plan, inputs, computerNameSelected: true, domainJoinRequested: false);
        _stateStore.SavePending(pending);
        var resumeResult = _resumeLauncher.RegisterResume();
        var warnings = new List<ProvisioningExecutionMessage>
        {
            new(
                "execution.restart.required.afterPartialFailure",
                "A previous computer-name change requires a restart. Easyaller saved a resume record that verifies only the completed rename."),
        };
        if (!resumeResult.IsSuccess)
        {
            warnings.Add(new ProvisioningExecutionMessage(
                resumeResult.ErrorCode ?? "execution.resume.register.failed",
                "Restart Windows, then reopen Easyaller with --resume-provisioning to verify the completed rename."));
        }

        return new ProvisioningExecutionResult(
            ProvisioningExecutionStatus.Failed,
            pending.ExecutionId,
            operations,
            [new ProvisioningExecutionMessage(result.ErrorCode ?? fallbackCode, "A Windows provisioning operation did not complete.", result.Detail)],
            warnings);
    }

    private static PendingProvisioningExecution CreatePending(
        ProvisioningPlan plan,
        RuntimeProvisioningInputs inputs,
        bool computerNameSelected,
        bool domainJoinRequested) =>
        new(
            Guid.NewGuid(),
            plan.ProfileId,
            plan.ProfileRevision,
            computerNameSelected ? inputs.ComputerName : null,
            computerNameSelected,
            domainJoinRequested,
            DateTimeOffset.UtcNow);
}
