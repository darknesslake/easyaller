# Temporary-account cleanup state machine

Easyaller now has a deterministic, in-memory state machine for deciding when a temporary provisioning account may be disabled or deleted. It does not call Windows account-management APIs, change a local account, or persist credentials.

## States

The state machine records these states in order when they apply:

1. `created`
2. `firstLogin`
3. `provisioning`
4. `domainJoined` when the flow requires a domain join
5. `validated`
6. `cleanupEligible`
7. `cleaned`

`domainJoined` is intentionally skipped for a flow that does not require domain join. It cannot be marked for such a flow.

## Cleanup gate

Final validation is accepted only when all required evidence is present:

- resume completed, when the deployment requires resume;
- domain join verified, when the deployment requires domain join;
- expected administrator access verified.

Only then can the state become `cleanupEligible`. The selected profile policy determines the planned action: `disable` or `delete`. A later Windows adapter must execute only that planned action and then report `cleaned`; it must never infer eligibility or bypass the validation gate.

This model deliberately has no fallback that deletes an account after a timer, a reboot, or a missing validation result. Windows SIM and disposable-VM tests are required before any real account-management adapter is introduced.
