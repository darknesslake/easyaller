using Easyaller.Core.Profiles;
using Easyaller.Core.Provisioning;

namespace Easyaller.Core.Tests;

public sealed class TemporaryAccountCleanupStateMachineTests
{
    [Fact]
    public void Apply_ValidatedResumeFlow_OnlyPlansDisableAfterAdministratorAccessIsVerified()
    {
        var machine = new TemporaryAccountCleanupStateMachine();
        var state = TemporaryAccountCleanupState.Create(
            "ProvisioningAdmin",
            new TemporaryAccountCleanupRequirements(
                ProvisioningAccountCleanupMode.DisableAfterValidation,
                RequiresDomainJoin: false,
                RequiresResume: true));

        state = Apply(machine, state, TemporaryAccountCleanupEvent.FirstLoginObserved);
        state = Apply(machine, state, TemporaryAccountCleanupEvent.ProvisioningStarted);
        var validated = machine.Apply(
            state,
            TemporaryAccountCleanupEvent.FinalValidationSucceeded,
            new TemporaryAccountValidationEvidence(
                ResumeCompleted: true,
                DomainJoinVerified: false,
                ExpectedAdministratorAccessVerified: true));
        var eligible = machine.Apply(validated.State, TemporaryAccountCleanupEvent.CleanupMarkedEligible);
        var cleaned = machine.Apply(eligible.State, TemporaryAccountCleanupEvent.CleanupCompleted);

        Assert.True(validated.IsAccepted);
        Assert.Equal(TemporaryAccountCleanupStage.Validated, validated.State.Stage);
        Assert.True(eligible.IsAccepted);
        Assert.Equal(TemporaryAccountCleanupAction.Disable, eligible.PlannedAction);
        Assert.True(cleaned.IsAccepted);
        Assert.Equal(TemporaryAccountCleanupStage.Cleaned, cleaned.State.Stage);
    }

    [Fact]
    public void Apply_DomainJoinRequirement_BlocksValidationUntilDomainAndAccessAreVerified()
    {
        var machine = new TemporaryAccountCleanupStateMachine();
        var state = TemporaryAccountCleanupState.Create(
            "ProvisioningAdmin",
            new TemporaryAccountCleanupRequirements(
                ProvisioningAccountCleanupMode.DeleteAfterValidation,
                RequiresDomainJoin: true,
                RequiresResume: true));

        state = Apply(machine, state, TemporaryAccountCleanupEvent.FirstLoginObserved);
        state = Apply(machine, state, TemporaryAccountCleanupEvent.ProvisioningStarted);
        var beforeDomainJoin = machine.Apply(
            state,
            TemporaryAccountCleanupEvent.FinalValidationSucceeded,
            new TemporaryAccountValidationEvidence(true, true, true));
        state = Apply(machine, state, TemporaryAccountCleanupEvent.DomainJoinConfirmed);
        var missingAccess = machine.Apply(
            state,
            TemporaryAccountCleanupEvent.FinalValidationSucceeded,
            new TemporaryAccountValidationEvidence(true, true, false));
        var validated = machine.Apply(
            state,
            TemporaryAccountCleanupEvent.FinalValidationSucceeded,
            new TemporaryAccountValidationEvidence(true, true, true));
        var eligible = machine.Apply(validated.State, TemporaryAccountCleanupEvent.CleanupMarkedEligible);

        Assert.False(beforeDomainJoin.IsAccepted);
        Assert.Equal("cleanup.validation.stage.invalid", beforeDomainJoin.ErrorCode);
        Assert.False(missingAccess.IsAccepted);
        Assert.Equal("cleanup.validation.administratorAccess.required", missingAccess.ErrorCode);
        Assert.True(validated.IsAccepted);
        Assert.Equal(TemporaryAccountCleanupAction.Delete, eligible.PlannedAction);
    }

    [Fact]
    public void Apply_MissingResumeOrOutOfOrderCleanup_IsRejectedWithoutChangingState()
    {
        var machine = new TemporaryAccountCleanupStateMachine();
        var state = TemporaryAccountCleanupState.Create(
            "ProvisioningAdmin",
            new TemporaryAccountCleanupRequirements(
                ProvisioningAccountCleanupMode.DisableAfterValidation,
                RequiresDomainJoin: false,
                RequiresResume: true));
        var invalidCleanup = machine.Apply(state, TemporaryAccountCleanupEvent.CleanupMarkedEligible);

        state = Apply(machine, state, TemporaryAccountCleanupEvent.FirstLoginObserved);
        state = Apply(machine, state, TemporaryAccountCleanupEvent.ProvisioningStarted);
        var missingResume = machine.Apply(
            state,
            TemporaryAccountCleanupEvent.FinalValidationSucceeded,
            new TemporaryAccountValidationEvidence(false, false, true));

        Assert.False(invalidCleanup.IsAccepted);
        Assert.Equal(TemporaryAccountCleanupStage.Created, invalidCleanup.State.Stage);
        Assert.Equal("cleanup.eligibility.validation.required", invalidCleanup.ErrorCode);
        Assert.False(missingResume.IsAccepted);
        Assert.Equal(TemporaryAccountCleanupStage.Provisioning, missingResume.State.Stage);
        Assert.Equal("cleanup.validation.resume.required", missingResume.ErrorCode);
    }

    [Fact]
    public void Create_EmptyAccountName_IsRejected()
    {
        var requirements = new TemporaryAccountCleanupRequirements(
            ProvisioningAccountCleanupMode.DisableAfterValidation,
            RequiresDomainJoin: false,
            RequiresResume: false);

        Assert.Throws<ArgumentException>(() => TemporaryAccountCleanupState.Create(" ", requirements));
    }

    private static TemporaryAccountCleanupState Apply(
        TemporaryAccountCleanupStateMachine machine,
        TemporaryAccountCleanupState state,
        TemporaryAccountCleanupEvent cleanupEvent)
    {
        var result = machine.Apply(state, cleanupEvent);
        Assert.True(result.IsAccepted, result.ErrorMessage);
        return result.State;
    }
}
