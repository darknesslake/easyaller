# Deployment operator guide

This guide is the single operational path for testing an Easyaller Windows deployment. Easyaller is pre-alpha software. Use it first in an isolated Windows 11 virtual machine, then on a dedicated non-production test PC. Do not use a corporate workstation, corporate credentials, or the only copy of important data.

Русская версия: [DEPLOYMENT_OPERATIONS_RU.md](DEPLOYMENT_OPERATIONS_RU.md).

## Current capability boundary

Easyaller can create a validated profile, generate a deterministic `autounattend.xml`, preview the deployment in memory, export a hash-verified package, and copy Windows Setup media plus that package to an already empty, preformatted removable volume. The USB flow requires an explicit eligible removable disk and the exact phrase `ERASE`.

It does not download, mount, alter, or validate an ISO; format, partition, or select an internal disk; join a domain during Windows Setup; apply desktop privacy policies; or manage a real temporary account. The desktop UI also cannot add a temporary account, payload, installer, script, or first-logon bootstrapper. Those latter paths are API contracts that require Windows SIM and VM evidence before use.

## Before the first test

1. Use Windows 11 Pro or Enterprise amd64, an official administrator-supplied ISO, one empty VM disk, and a checkpoint named `before-oobe`. For a physical pilot, replace the VM only after the VM test passes and use a dedicated test PC plus a recoverable USB.
2. Inspect the ISO read-only and record its SHA-256, edition, display version, build, and selected image index. Follow [ISO_INSPECTION.md](ISO_INSPECTION.md) and [WINDOWS_SIM_VALIDATION.md](WINDOWS_SIM_VALIDATION.md).
3. In Easyaller, create or import a neutral profile. Review its locale, time zone, OOBE choices, and compatibility result. An unknown version or build is a warning, not evidence that the target works.
4. Create a dry run, export a package to a new folder outside Git, and retain its `deployment-manifest.json`. Do not commit profiles, packages, ISO files, evidence, account passwords, or screenshots containing confidential data.

## Prepare the installation USB

1. Mount or extract the already approved ISO without modifying it. Select its Windows Setup root directory and the exported package in **Create installation USB**.
2. Connect one empty, preformatted, removable test USB. The screen deliberately shows only eligible removable disks and never selects a target by default.
3. Select the target, create the plan, compare vendor, serial or immutable device ID, disk number, and size with the physical device, then type `ERASE` exactly.
4. Wait for the final hash verification. A ready result means the written files matched the plan. It is not proof that firmware boots the device or that Windows Setup accepts the answer file.

The workflow rejects a nonempty volume, a changed or unsafe disk, ambiguous drive-letter binding, source changes, collisions, copy errors, and output hash mismatches. It never formats or partitions the USB. See [USB_CREATOR.md](USB_CREATOR.md) for the complete media safety contract.

## Windows Setup and answer-file discovery

The generated package places `autounattend.xml` at the package root. When the package is copied to the installation USB, the file is therefore at the USB root. Microsoft documents that Windows Setup looks for a root-level file named `Autounattend.xml` on removable media when no other answer file was selected. The filename is case-insensitive on the usual Windows file systems, but Easyaller preserves the generated lowercase filename. Do not place a second answer file on any connected removable media during testing.

Windows Setup can also be started from WinPE with an explicit `/unattend:<path>` argument. That is an advanced operator path, not something the Easyaller UI launches. See [Automate Windows Setup](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/automate-windows-setup?view=windows-11) and [Windows Setup command-line options](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/windows-setup-command-line-options?view=windows-11).

## Install Windows safely

1. Boot the disposable VM or test PC from the completed USB in UEFI mode.
2. At the disk-selection page, manually select the one intended empty target disk. Easyaller does not generate disk configuration and never chooses an internal disk.
3. Observe every Windows Setup and OOBE page. The answer file contains only explicitly configured documented settings. Missing or invalid required settings can cause Setup to show UI, which is expected behavior rather than a signal to add an undocumented bypass.
4. If Windows displays a page that the profile did not intend to automate, record the page, target build, ISO hash, and relevant logs. Do not add `BYPASSNRO`, `SkipMachineOOBE`, `SkipUserOOBE`, UI automation, or synthetic keystrokes.
5. Complete any remaining Windows UI manually. Do not join a production domain or enter production credentials during the pilot.

## Temporary account, first boot, and cleanup

The desktop workflow does not currently create a temporary local account. If a later API-driven package is used, `ProvisioningAdmin` is the only supported temporary account name, its password is generated per package, may be shown once, and must never be placed in Git, evidence, or ordinary logs. `PlainText=false` in an answer file is obfuscation, not encryption.

AutoLogon is intentionally absent. The first-logon bootstrapper is also not enabled from the UI. Its explicit API path requires a manually signed-in `ProvisioningAdmin`, a manifest-verified Easyaller payload, Windows SIM validation, and disposable-VM validation. Read [FIRST_LOGON_BOOTSTRAP.md](FIRST_LOGON_BOOTSTRAP.md) before enabling it.

Temporary-account cleanup is currently a state machine only. It cannot disable or delete a Windows account. A future adapter may clean up only after required resume, domain, final-validation, and administrator-access evidence exists. See [TEMPORARY_ACCOUNT_CLEANUP.md](TEMPORARY_ACCOUNT_CLEANUP.md).

## Evidence, logs, and compatibility

Record the test date, host or VM model, firmware mode, ISO SHA-256, image index, edition, display version, build, profile revision, package-manifest hash, selected disk type, observed OOBE pages, result, and a short failure description. Keep this evidence outside Git.

For Setup failures, collect `setuperr.log` first and then `setupact.log`. Before disk configuration, Windows Setup logs are available under `X:\Windows\Panther`; after it, use `%WINDIR%\Panther`. OOBE-related answer-file processing can also be investigated under `%WINDIR%\Panther\UnattendGC`. Redact account names, product keys, serial identifiers, and organization data before sharing logs. Microsoft documents these log locations in [Windows Setup log files and event logs](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/windows-setup-log-files-and-event-logs?view=windows-11).

The current catalog lists Windows 11 24H2 build 26100 and 25H2 build 26200 for Pro and Enterprise amd64 as documented only. Neither entry is yet Windows SIM validated or VM validated. See [COMPATIBILITY.md](COMPATIBILITY.md).

## Troubleshooting

| Symptom | Safe response |
| --- | --- |
| The USB screen shows no disk | Confirm it is a removable USB, SD, or MMC device, online, writable, and not the system or boot disk. Do not work around the filter by pointing the app at a drive letter. |
| The app rejects the USB root | Empty and preformat the dedicated test volume outside Easyaller, then recheck that it belongs to the selected disk and has exactly one drive-letter partition. Easyaller will not prepare it. |
| The hash check fails or the source changed | Discard the incomplete USB layout, recreate the package if necessary, and start again with the same verified sources. Do not manually patch files after the plan. |
| Setup ignores the intended automation | Confirm that only one removable answer file is attached, `autounattend.xml` is at the USB root, the selected ISO image matches the validation input, and inspect Panther logs. |
| Setup stops at an OOBE page | Complete the page manually, preserve evidence, and treat the target as unvalidated. Do not introduce undocumented bypasses. |
| An unknown build shows a warning | Stop the rollout after the disposable test. Add Windows SIM and VM evidence before treating it as compatible. |
| First-logon or cleanup behavior is expected | It is not available through the desktop flow. Do not simulate it with hand-written scripts or account deletion. |

## Exit criteria

Do not proceed to a physical-PC pilot until the exact ISO, image index, answer file, Windows edition, display version, and build have a clean Windows SIM result and a passing isolated VM run. A physical pilot remains WP-062 and requires its own recorded evidence.
