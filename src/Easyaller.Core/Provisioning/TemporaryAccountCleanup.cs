using Easyaller.Core.Profiles;

namespace Easyaller.Core.Provisioning;

public enum TemporaryAccountCleanupStage
{
    Created,
    FirstLogin,
    Provisioning,
    DomainJoined,
    Validated,
    CleanupEligible,
    Cleaned,
}

public enum TemporaryAccountCleanupEvent
{
    FirstLoginObserved,
    ProvisioningStarted,
    DomainJoinConfirmed,
    FinalValidationSucceeded,
    CleanupMarkedEligible,
    CleanupCompleted,
}

public enum TemporaryAccountCleanupAction
{
    Disable,
    Delete,
}

public sealed record TemporaryAccountCleanupRequirements(
    ProvisioningAccountCleanupMode Mode,
    bool RequiresDomainJoin,
    bool RequiresResume);

public sealed record TemporaryAccountValidationEvidence(
    bool ResumeCompleted,
    bool DomainJoinVerified,
    bool ExpectedAdministratorAccessVerified);

public sealed record TemporaryAccountCleanupState(
    string AccountName,
    TemporaryAccountCleanupRequirements Requirements,
    TemporaryAccountCleanupStage Stage,
    TemporaryAccountValidationEvidence? ValidationEvidence = null)
{
    public static TemporaryAccountCleanupState Create(
        string accountName,
        TemporaryAccountCleanupRequirements requirements)
    {
        if (string.IsNullOrWhiteSpace(accountName))
        {
            throw new ArgumentException("Temporary account name is required.", nameof(accountName));
        }

        ArgumentNullException.ThrowIfNull(requirements);
        return new TemporaryAccountCleanupState(accountName, requirements, TemporaryAccountCleanupStage.Created);
    }
}

public sealed record TemporaryAccountCleanupTransitionResult(
    TemporaryAccountCleanupState State,
    TemporaryAccountCleanupAction? PlannedAction,
    string? ErrorCode,
    string? ErrorMessage)
{
    public bool IsAccepted => ErrorCode is null;
}

public sealed class TemporaryAccountCleanupStateMachine
{
    public TemporaryAccountCleanupTransitionResult Apply(
        TemporaryAccountCleanupState state,
        TemporaryAccountCleanupEvent cleanupEvent,
        TemporaryAccountValidationEvidence? evidence = null)
    {
        ArgumentNullException.ThrowIfNull(state);

        return cleanupEvent switch
        {
            TemporaryAccountCleanupEvent.FirstLoginObserved => Advance(
                state,
                TemporaryAccountCleanupStage.Created,
                TemporaryAccountCleanupStage.FirstLogin),
            TemporaryAccountCleanupEvent.ProvisioningStarted => Advance(
                state,
                TemporaryAccountCleanupStage.FirstLogin,
                TemporaryAccountCleanupStage.Provisioning),
            TemporaryAccountCleanupEvent.DomainJoinConfirmed => ConfirmDomainJoin(state),
            TemporaryAccountCleanupEvent.FinalValidationSucceeded => CompleteValidation(state, evidence),
            TemporaryAccountCleanupEvent.CleanupMarkedEligible => MarkCleanupEligible(state),
            TemporaryAccountCleanupEvent.CleanupCompleted => CompleteCleanup(state),
            _ => Rejected(state, "cleanup.event.unsupported", "Temporary-account cleanup event is not supported."),
        };
    }

    private static TemporaryAccountCleanupTransitionResult ConfirmDomainJoin(TemporaryAccountCleanupState state)
    {
        if (!state.Requirements.RequiresDomainJoin)
        {
            return Rejected(
                state,
                "cleanup.domainJoin.notRequired",
                "Domain join cannot be confirmed when the cleanup flow does not require it.");
        }

        return Advance(state, TemporaryAccountCleanupStage.Provisioning, TemporaryAccountCleanupStage.DomainJoined);
    }

    private static TemporaryAccountCleanupTransitionResult CompleteValidation(
        TemporaryAccountCleanupState state,
        TemporaryAccountValidationEvidence? evidence)
    {
        var requiredPreviousStage = state.Requirements.RequiresDomainJoin
            ? TemporaryAccountCleanupStage.DomainJoined
            : TemporaryAccountCleanupStage.Provisioning;
        if (state.Stage != requiredPreviousStage)
        {
            return Rejected(
                state,
                "cleanup.validation.stage.invalid",
                "Final validation can begin only after the required provisioning stages are complete.");
        }

        if (evidence is null)
        {
            return Rejected(state, "cleanup.validation.evidence.required", "Final validation evidence is required.");
        }

        if (state.Requirements.RequiresResume && !evidence.ResumeCompleted)
        {
            return Rejected(state, "cleanup.validation.resume.required", "Cleanup requires a completed resume before final validation.");
        }

        if (state.Requirements.RequiresDomainJoin && !evidence.DomainJoinVerified)
        {
            return Rejected(state, "cleanup.validation.domainJoin.required", "Cleanup requires a verified domain join before final validation.");
        }

        if (!evidence.ExpectedAdministratorAccessVerified)
        {
            return Rejected(
                state,
                "cleanup.validation.administratorAccess.required",
                "Cleanup requires verified expected administrator access.");
        }

        return Accepted(state with
        {
            Stage = TemporaryAccountCleanupStage.Validated,
            ValidationEvidence = evidence,
        });
    }

    private static TemporaryAccountCleanupTransitionResult MarkCleanupEligible(TemporaryAccountCleanupState state)
    {
        if (state.Stage != TemporaryAccountCleanupStage.Validated ||
            state.ValidationEvidence is not { ExpectedAdministratorAccessVerified: true })
        {
            return Rejected(
                state,
                "cleanup.eligibility.validation.required",
                "Temporary-account cleanup requires successful final validation and verified administrator access.");
        }

        var action = state.Requirements.Mode == ProvisioningAccountCleanupMode.DeleteAfterValidation
            ? TemporaryAccountCleanupAction.Delete
            : TemporaryAccountCleanupAction.Disable;
        return Accepted(state with { Stage = TemporaryAccountCleanupStage.CleanupEligible }, action);
    }

    private static TemporaryAccountCleanupTransitionResult CompleteCleanup(TemporaryAccountCleanupState state) =>
        Advance(state, TemporaryAccountCleanupStage.CleanupEligible, TemporaryAccountCleanupStage.Cleaned);

    private static TemporaryAccountCleanupTransitionResult Advance(
        TemporaryAccountCleanupState state,
        TemporaryAccountCleanupStage expectedStage,
        TemporaryAccountCleanupStage nextStage) =>
        state.Stage == expectedStage
            ? Accepted(state with { Stage = nextStage })
            : Rejected(
                state,
                "cleanup.transition.invalid",
                $"Cannot transition from {state.Stage} to {nextStage}.");

    private static TemporaryAccountCleanupTransitionResult Accepted(
        TemporaryAccountCleanupState state,
        TemporaryAccountCleanupAction? action = null) =>
        new(state, action, null, null);

    private static TemporaryAccountCleanupTransitionResult Rejected(
        TemporaryAccountCleanupState state,
        string code,
        string message) =>
        new(state, null, code, message);
}
