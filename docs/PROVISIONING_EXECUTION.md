# Runtime provisioning execution

Easyaller can now apply the runtime values from a selected profile through **Set up this PC**. This is a Windows-only pre-alpha path. It has mock and integration-style state-machine tests, but it has not yet been exercised in a Windows VM or on a physical PC.

Русская версия: [PROVISIONING_EXECUTION_RU.md](PROVISIONING_EXECUTION_RU.md).

## Explicit boundary

The screen first validates runtime values without changing Windows. Applying them requires an explicit exact `APPLY` confirmation and administrator privileges. The reusable profile still contains no password, token, or domain credential. A runtime domain credential exists only in memory for the fixed domain-join operation, is redacted by string output, and is disposed after the operation.

The executor accepts only these fixed operations, in this order:

1. Verify that exactly one enabled network adapter matches the entered adapter name or interface GUID. For an explicit `staticIpv4` profile only, apply the validated IPv4 address, subnet mask, gateway, and DNS server list to that adapter, then reread the result. It does not set Wi-Fi credentials or invent routes.
2. When the profile requests a proxy, set the Windows WinHTTP proxy from the runtime value.
3. Rename the local computer.
4. When the operator enters both a domain name and short-lived credential, join that domain.

The executor does not format disks, modify ISO files, choose an internal disk, add installers, run profile-provided commands, apply unspecified privacy preferences, or invent network settings. It does not use OOBE bypasses.

## Apply and resume flow

1. Run Easyaller as an administrator on Windows.
2. Select a profile, enter the requested runtime values, and use **Validate entered values**.
3. Review the target carefully, type `APPLY` exactly, then select **Apply to Windows**.
4. If a computer rename or domain join succeeds, Easyaller stores a small pending-resume record under `%ProgramData%\Easyaller\state`. It contains only execution ID, profile ID and revision, expected computer name, whether a domain join was requested, and creation time. It never contains a password, proxy, or domain name.
5. Easyaller attempts to register one fixed `RunOnce` continuation for `--resume-provisioning`. If the command cannot be registered, restart manually and launch Easyaller with that argument. Do not edit the state file.
6. After restart, resume verifies the final computer name and, when relevant, that the computer belongs to a domain before removing the pending state.

Windows only processes machine-wide `RunOnce` entries when an administrator signs in after reboot. The entry is bounded to the current Easyaller executable command and is not a general command runner. See Microsoft's [Run and RunOnce registry keys](https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys).

## Documented Windows mechanisms

The Windows adapter runs fixed `Rename-Computer`, `Add-Computer`, `Set-WinhttpProxy`, `Set-NetIPInterface`, `New-NetIPAddress`, and `Set-DnsClientServerAddress` commands with profile or runtime values supplied outside the command text. It never passes credentials in a command line or records standard output or error text. See [`docs/STATIC_IPV4_DNS.md`](STATIC_IPV4_DNS.md) for the static-network boundary and Microsoft references.

## Stop conditions

Easyaller stops without continuing when the confirmation does not match, runtime validation fails, Windows or administrator access is unavailable, the network adapter is missing, disabled, or ambiguous, or a fixed operation fails. A failure after an earlier operation may leave that earlier Windows change in place. The app reports only a generic failure code and deliberately does not include PowerShell output that could contain organization-specific data.

Do not use this path against a production domain or workstation until WP-041 has recorded Windows SIM and VM evidence for the exact target build. The first physical use remains WP-062.
