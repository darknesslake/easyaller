# VM testing guide

This guide is the required workflow for validating an Easyaller deployment package in an isolated Windows 11 virtual machine. It does not authorize testing on physical workstations, physical disks, production domains, or removable media.

## Prerequisites

- Use a Windows 11 VM that meets the documented VM requirements: Generation 2, UEFI, Secure Boot, vTPM 2.0, at least 4 GB memory, at least 64 GB virtual storage, and two virtual processors.
- Use a hypervisor checkpoint or snapshot feature. It is a test reset point, not a backup for production data.
- Obtain the ISO from an administrator or other approved official Microsoft source. Do not download an ISO through Easyaller and do not commit it to Git.
- Keep the ISO, generated package, VM disk, screenshots, logs, and evidence outside this repository. Record the SHA-256 hash of every ISO, `install.wim` or `install.esd`, answer file, and deployment manifest.
- Complete the applicable Windows SIM validation first. Windows SIM success does not replace VM testing.

Microsoft documents Windows 11 VM requirements and Hyper-V checkpoints: [Windows 11 requirements](https://learn.microsoft.com/en-us/windows/whats-new/windows-11-requirements), [Hyper-V checkpoints](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/checkpoints).

## Isolated VM setup

1. Create a new empty VM with one virtual disk only. Do not attach a host disk, USB drive, physical disk, or production network share.
2. Configure the VM for the ISO and required UEFI, Secure Boot, vTPM, memory, storage, and CPU settings.
3. Use an isolated or NAT test network. Disconnect it when a scenario requires offline OOBE. Never use production domain credentials.
4. Copy the tested package by an isolated mechanism and verify its deployment-manifest hash before booting the VM.
5. Create a checkpoint named `before-oobe` after the VM hardware and media are ready but before Windows Setup starts.

## Test sequence

1. Boot Windows Setup and manually choose the single empty VM disk after confirming its virtual-disk identity and size. Easyaller does not automate internal-disk selection.
2. Observe OOBE. Record the actual edition, display version, build, architecture, locale, and every configured OOBE screen outcome.
3. Sign in manually with the temporary `ProvisioningAdmin` account only when the generated package created it. Record that the password was shown once, but never record the password itself.
4. Confirm the payload manifest verification, first-logon launch, and resume behavior. Create a second checkpoint named `after-first-login` before any destructive or cleanup experiment.
5. Test network, proxy, application, and instruction flows only with neutral test values and fixtures. Do not add the VM to a production domain.
6. For a mock domain-join scenario, record only a simulated success or failure result in the evidence. Do not run domain-join commands and do not supply directory credentials until a dedicated executor and its approval exist.
7. Exercise cleanup only after the expected administrator-access evidence is available. The current state machine plans cleanup but has no Windows account-management adapter.
8. Compare the runtime version gate with the package manifest. A mismatch must block validated actions; an unknown build must warn and skip them.

## Evidence and stop conditions

Copy [`fixtures/vm-evidence.template.json`](fixtures/vm-evidence.template.json) to a local evidence directory such as `%LOCALAPPDATA%\Easyaller\Validation\VM`. Replace placeholders with facts from one VM run. The template is safe to keep in Git; completed evidence is not.

Stop the run and preserve the checkpoint when any of these happens:

- Windows Setup exposes a disk other than the expected empty VM disk.
- The ISO, image, answer file, manifest, or payload hash differs from the recorded input.
- Windows SIM has warnings or errors for the same input set.
- The runtime gate reports `blocked` or an unexpected warning.
- A script requests a secret, production credential, arbitrary command, AutoLogon, disk operation, or network download.

Do not call the package VM-validated merely because Windows installed. Mark a compatibility entry `VmValidated` only after the full planned matrix records passing evidence for the exact ISO and package inputs.

## Reset and test-secret cleanup

After each run, either apply the `before-oobe` checkpoint or delete the VM and its virtual-disk chain. Remove the local copy of the generated package and completed evidence if it includes organization-specific data. Delete test-only accounts from the VM only through an approved cleanup flow after administrator access is confirmed.

Never place generated temporary-account passwords, runtime domain credentials, tokens, VM disks, snapshots, screenshots, or completed evidence in the repository, issue tracker, or public release assets.
