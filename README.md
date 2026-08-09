# Easyaller

Easyaller is an open-source Windows workstation provisioning tool. It separates reusable, versioned configuration profiles from sensitive per-machine deployment packages, so teams can share setup standards without committing credentials or local configuration to Git.

> Status: pre-alpha. The profile contract, validation foundation, local profile storage, import/export service, deterministic answer-file generation, in-memory dry run, safe file-only deployment-package export, its desktop workflow, configuration-set payload delivery, fixed first-logon bootstrapper, temporary-account cleanup state machine, explicit privacy-policy service, runtime Windows version gate, read-only removable-disk safety model, read-only ISO inspection, and one-time USB confirmation state machine are implemented. Completed Windows SIM and VM validation, real account cleanup, desktop policy application, first-boot execution, and USB writing are not implemented yet.

## Interface previews

These are static pre-alpha interface previews, created before the current Russian desktop UI. They are not screenshots of the completed application.

![Easyaller Profiles preview](docs/assets/profiles-preview.png)

![Easyaller Prepare Windows 11 preview](docs/assets/prepare-windows-preview.png)

## What Easyaller is for

- Build and exchange portable workstation profiles.
- Require the final computer name, network settings, proxy, and domain credentials at runtime.
- Prepare a Windows 11 deployment package using documented Windows Setup mechanisms.
- Reuse one provisioning pipeline after Windows first boot rather than maintaining a second deployment engine.
- Make destructive USB creation an explicit, separately protected workflow.

## Safety model

- Reusable profiles must never contain passwords, tokens, or domain credentials.
- Imported JSON is untrusted input. The loader rejects duplicate keys, unknown fields, invalid enum values, and unsupported schema versions.
- Application paths are constrained to the deployment package. Path traversal and absolute paths are rejected.
- A per-machine deployment package is treated as sensitive and is ignored by Git.
- Disk partitioning and USB formatting are out of scope for the current implementation.

## Implemented today

- Versioned `*.wpprofile.json` profile domain model.
- JSON Schema v1 in [`schemas/provisioning-profile.schema.json`](schemas/provisioning-profile.schema.json).
- Deterministic UTF-8 JSON serialization with a stable property order and a final newline.
- Strict profile loading with schema-version, duplicate-property, required-field, locale, OOBE, and package-path validation.
- Local profile repository with revision-conflict detection, atomic writes, recoverable backups, and corrupted-file isolation.
- Import/export service with UTF-8 and size limits, secret-field scanning, strict loading, conflict resolution, export previews, and atomic file output.
- Provisioning-plan contract that maps a validated profile to future setup steps and explicit runtime prompts without executing Windows changes.
- Avalonia desktop screen for listing, creating, cloning, editing all current profile sections, refreshing, importing, exporting, and explicitly deleting local profiles.
- Set up this PC screen that previews the selected profile plan, validates runtime-only computer, network, proxy, and domain input without applying it, and prepares a basic file-only Windows 11 package through preview, dry run, and explicit folder selection.
- Deployment module contracts for compatibility validation, answer-file generation, in-memory dry run, package planning, and safe file-only export.
- In-memory deployment dry run exposes the effective profile, OOBE and privacy choices, compatibility status, and the exact generated XML without writing files or changing Windows. It warns that profile data and any obfuscated temporary-account password must be treated as sensitive.
- Deterministic, XML-writer-based `autounattend.xml` generation for validated locale, time-zone, explicitly configured OOBE values, and an optional ephemeral local account. The desktop UI does not expose it yet.
- Safe deployment-package exporter: writes to a sibling staging directory, verifies SHA-256 hashes, then atomically finalizes a new destination. Packages contain the answer file, selected profile, README, manifest, and explicitly allowed local payload, scripts, and installers. See [`docs/DEPLOYMENT_PACKAGE.md`](docs/DEPLOYMENT_PACKAGE.md).
- Configuration-set payload layout keeps optional files below `$OEM$/$1/ProgramData/Easyaller` and writes a second payload manifest there. The read-only verifier detects missing or changed delivered files before future first-boot work uses them.
- Opt-in first-logon bootstrapper for the temporary `ProvisioningAdmin` account. It uses one fixed `FirstLogonCommands` command, verifies the payload, registers a one-time resume entry, launches the verified Easyaller application payload, and records completed startup after the main window is created. See [`docs/FIRST_LOGON_BOOTSTRAP.md`](docs/FIRST_LOGON_BOOTSTRAP.md).
- Temporary-account cleanup state machine: plans `disable` or `delete` only after the required resume, domain join, and expected administrator-access evidence. It has no Windows account-management adapter yet. See [`docs/TEMPORARY_ACCOUNT_CLEANUP.md`](docs/TEMPORARY_ACCOUNT_CLEANUP.md).
- Explicit privacy-policy service for documented location, advertising ID, and online speech settings. It supports only guarded Windows 11 targets, rereads every value after application, and keeps `notConfigured` and `userChoice` as no-ops. The desktop UI does not call it yet. See [`docs/PRIVACY_POLICIES.md`](docs/PRIVACY_POLICIES.md).
- Read-only runtime Windows version gate compares the installed system with the deployment manifest and selected profile. Unknown builds warn and skip validated actions; mismatches block them. The desktop UI does not call it yet. See [`docs/RUNTIME_VERSION_GATE.md`](docs/RUNTIME_VERSION_GATE.md).
- Read-only removable-disk inventory and hot-swap safety model. It never selects a default target and blocks system, boot, fixed, offline, read-only, or identity-less disks. See [`docs/DISK_SAFETY.md`](docs/DISK_SAFETY.md).
- Read-only Windows ISO inspection calculates SHA-256, validates media structure, editions, architecture, and a configurable size cap. It mounts only with read-only access and always attempts to dismount in `finally`; the desktop UI does not call it yet. See [`docs/ISO_INSPECTION.md`](docs/ISO_INSPECTION.md).
- One-time, five-minute destructive USB confirmation state machine. It displays the device identity, requires the exact phrase `ERASE`, and rechecks the selected disk immediately before a future first write. See [`docs/USB_CONFIRMATION.md`](docs/USB_CONFIRMATION.md).
- Cryptographically generated 24-character temporary local-account password with one-time reveal and memory cleanup. It is never profile or manifest data, and AutoLogon remains excluded.
- Windows-host validation harness that records ISO, image, and answer-file hashes plus explicit Windows SIM evidence without mounting images or touching disks. See [`docs/WINDOWS_SIM_VALIDATION.md`](docs/WINDOWS_SIM_VALIDATION.md).
- Initial Windows 11 compatibility catalog for documented Pro and Enterprise amd64 24H2 and 25H2 targets. See [`docs/COMPATIBILITY.md`](docs/COMPATIBILITY.md).
- Neutral profile fixtures and unit tests.

## Repository layout

```text
src/
  Easyaller.App/           Avalonia desktop application
  Easyaller.Core/          Profile model, validation, serialization, local storage
  Easyaller.Deployment/    Future Windows deployment module
tests/
  Easyaller.Core.Tests/    Unit tests and neutral JSON fixtures
schemas/                   Public profile contract
docs/previews/             Source for the interface previews
docs/assets/               README screenshots
```

## Development

Prerequisite: .NET SDK 10.

```sh
dotnet build Easyaller.slnx
dotnet test Easyaller.slnx --no-build
```

Run the desktop application:

```sh
dotnet run --project src/Easyaller.App/Easyaller.App.csproj
```

On Windows, profiles are stored in `%ProgramData%\Easyaller\Profiles`. On other desktop platforms, the pre-alpha app uses the current user's local application-data directory.

Local organization configuration, real exported profiles, deployment output, ISO files, VM disks, installers, and build output are ignored by Git. Only neutral test fixtures and future public examples may use the `*.wpprofile.json` extension in the repository.

Read [`PRODUCT_SPEC.md`](PRODUCT_SPEC.md) for product decisions.

Read [`docs/GETTING_STARTED.md`](docs/GETTING_STARTED.md) for a practical guide to running the desktop application, creating profiles, importing and exporting them, and understanding the current pre-alpha limits.

Русская инструкция: [`docs/GETTING_STARTED_RU.md`](docs/GETTING_STARTED_RU.md).

Windows-host validation instructions: [`docs/WINDOWS_SIM_VALIDATION.md`](docs/WINDOWS_SIM_VALIDATION.md) and [`docs/WINDOWS_SIM_VALIDATION_RU.md`](docs/WINDOWS_SIM_VALIDATION_RU.md).

VM validation instructions: [`docs/VM_TESTING.md`](docs/VM_TESTING.md) and [`docs/VM_TESTING_RU.md`](docs/VM_TESTING_RU.md).

Removable-disk safety instructions: [`docs/DISK_SAFETY.md`](docs/DISK_SAFETY.md) and [`docs/DISK_SAFETY_RU.md`](docs/DISK_SAFETY_RU.md).

ISO inspection instructions: [`docs/ISO_INSPECTION.md`](docs/ISO_INSPECTION.md) and [`docs/ISO_INSPECTION_RU.md`](docs/ISO_INSPECTION_RU.md).

USB confirmation instructions: [`docs/USB_CONFIRMATION.md`](docs/USB_CONFIRMATION.md) and [`docs/USB_CONFIRMATION_RU.md`](docs/USB_CONFIRMATION_RU.md).

Deployment package format and safety instructions: [`docs/DEPLOYMENT_PACKAGE.md`](docs/DEPLOYMENT_PACKAGE.md) and [`docs/DEPLOYMENT_PACKAGE_RU.md`](docs/DEPLOYMENT_PACKAGE_RU.md).

## License

[MIT](LICENSE)
