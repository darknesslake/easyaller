# Easyaller implementation queue

Complete one task at a time, in order. Each task must end with focused tests and a concise handoff: changed files, decisions, checks, limitations, and the next task ID.

## Working protocol

Before a task, read `PRODUCT_SPEC.md`, this file, and any existing `AGENTS.md` or `HANDOFF.md`. Inspect `git status`; do not touch unrelated changes. Reuse existing services before adding a parallel pipeline.

During a task, do not add organization-specific defaults, credentials, random ISO downloads, or unvalidated Windows workarounds. Treat imported profiles as untrusted input. Do not implement destructive disk operations before their dedicated safety tasks.

After a task, run the repository build and tests, update this checklist, and preserve an exact blocker if the acceptance criteria are not met.

## Phase 0: foundation

### [x] WP-000: Initialize repository and baseline

Created the .NET 10 solution with `Easyaller.Core`, `Easyaller.Deployment`, and `Easyaller.Core.Tests`. Enabled nullable references and warnings as errors. Added Git ignores for local configuration, real profiles, deployment output, ISO files, VM disks, installers, and build output.

Verified: `dotnet build Easyaller.slnx` and `dotnet test Easyaller.slnx --no-build`.

### [x] WP-001: Keep the foundation neutral

The solution, namespaces, default profile, paths, fixtures, documentation, and interface previews use Easyaller only. No organization-specific domain, network, proxy, installer, or privacy defaults exist.

### [x] WP-010: Profile model and secret boundaries

Implemented a versioned profile model with separate Windows, machine, domain, applications, instructions, deployment, and cleanup sections. The model has no password or token fields; computer name and domain credentials are runtime-only. The validator checks locales, OOBE consistency, application paths, and common invalid states.

### [x] WP-011: JSON Schema v1 and deterministic serialization

Implemented `schemas/provisioning-profile.schema.json`, deterministic UTF-8 JSON, strict property ordering, duplicate-key detection, required-field enforcement, enum validation, unsupported-version rejection, and neutral valid/invalid fixtures.

## Phase 1: reusable profiles

### [x] WP-012: Profile repository and local storage

Implement `IProfileRepository` and a production store outside the binary directory, normally `%ProgramData%\Easyaller\Profiles`.

Acceptance criteria:

- List, read, create, update, clone, and delete profiles by `profileId`.
- Use revision checks to detect lost updates.
- Use temp-write, atomic rename, and recoverable backup.
- Isolate corrupted files instead of crashing the application.
- Unit tests use a temporary directory and cover write interruption and conflicts.

Implemented `IProfileRepository` and `FileProfileRepository`. The production root defaults to `%ProgramData%\\Easyaller\\Profiles`; tests use a temporary directory. Writes use a same-directory temporary file, atomic rename, and a `.backup.json` recovery file. Invalid local profile files move to `Corrupted` and are reported without stopping the profile list. Updates and deletes require the caller's expected revision.

Verified: `dotnet test Easyaller.slnx` with 20 passing tests.

### [x] WP-013: Import and export application service

Build the full import pipeline: size and UTF-8 limits, strict loader, schema, migration hook, semantic validation, secret scan, path checks, preview, conflict choice, and atomic save.

Acceptance criteria:

- Import never executes content or changes Windows.
- `password`, token-like fields, raw commands, absolute paths, and traversal paths stop the import with a clear field path.
- Export preview identifies confidential non-secret fields.
- Tests cover malformed UTF-8, duplicate keys, future versions, collisions, cancellation, and round trips.

Implemented `ProfileImportExportService` with a 1 MiB default size limit, strict UTF-8 decoding, migration-pipeline seam, secret and raw-command field scan, strict JSON loader, semantic validation, repository conflict preview, cancellation, copy and replace choices, confidential-field preview, deterministic export, and atomic file output. Imports do not execute content or change Windows.

Verified: `dotnet test Easyaller.slnx` with 30 passing tests.

### [x] WP-014: Profile management UI

Add the first functional desktop UI for profile list, editor, validation, cloning, import preview, export preview, and revision conflicts.

Acceptance criteria:

- All visible desktop-interface strings are Russian.
- Credentials cannot be entered in a reusable profile.
- The functional UI matches the documented safety model, not only the static preview.
- Core user states have UI or view-model tests.

Implemented the first functional cross-platform Avalonia profile manager. It lists, creates, clones, edits, refreshes, imports, exports, and explicitly deletes local profiles. The editor covers metadata, Windows target, locale, OOBE, privacy preference, machine prefix, proxy mode, domain mode, launch mode, temporary-account cleanup, applications, and instructions. Import uses preview and explicit conflict choice; export shows a confidential-field count and needs confirmation. Credentials have no profile input field. The current desktop interface is Russian. `ProfileEditorController` tests cover metadata validation, Windows and machine settings, privacy preferences, applications, and instructions.

Verified: `dotnet build Easyaller.slnx` with zero warnings and `dotnet test Easyaller.slnx --no-build` with 34 passing tests.

### [x] WP-015: Connect a selected profile to the provisioning pipeline

Map a selected profile into the existing setup pipeline without creating a second engine. Collect final computer name, adapter choice, network values, and credentials only at runtime.

Acceptance criteria:

- `notConfigured` values perform no system change.
- Credentials are short-lived in memory and redacted in logs.
- Reboot and resume behavior has integration coverage.

Implemented the shared `ProvisioningPlan` contract and builder, plus the Set up this PC desktop screen. A valid profile maps to declarative steps and explicit runtime prompts for computer name, adapter and network choice, proxy, and domain join. The screen previews the same plan and validates runtime input without changing Windows. `notConfigured` preferences produce no system-change step. Runtime domain credentials are an in-memory disposable object with redacted string output and the password field is cleared after validation. An optional proxy bypass list belongs to the reusable profile while the proxy address stays runtime-only.

Added `ProvisioningExecutionService` with an immutable operation order, exact `APPLY` confirmation, runtime revalidation, generic failure messages, and a pending-resume state that excludes passwords, proxies, and domain names. The Windows adapter verifies one enabled adapter, sets WinHTTP proxy only when requested, renames the computer, and optionally invokes documented domain join with the credential written only to the fixed child process standard input. It requests no automatic reboot; successful rename or domain join registers one constrained `RunOnce` resume where possible and the resumed app verifies the computer name and domain membership before clearing state. The Russian UI exposes this path separately from dry run and package export. It is untested on real Windows, so VM evidence remains mandatory before workstation use. English and Russian safety notes are in `docs/PROVISIONING_EXECUTION.md` and `docs/PROVISIONING_EXECUTION_RU.md`.

Verified: `dotnet build Easyaller.slnx` with zero warnings and `dotnet test Easyaller.slnx --no-build` with 122 passing tests. Execution order, no-op behavior for `notConfigured`, one-time confirmation, secret-free resume state, failed verification retention, partial-failure resume, and resume verification are covered with mock adapters and state stores.

Verified: `dotnet test Easyaller.slnx` with 38 passing tests.

## Phase 2: Windows deployment package

### [x] WP-020: Deployment module contracts

Define models and interfaces for compatibility, validation, XML generation, preview, dry run, and package export. Keep the deployment module independent from desktop UI.

Implemented `Easyaller.Deployment` contracts and a file-only preview path. `DeploymentPreviewService` composes the shared provisioning plan with target edition, architecture, version, and build validation. The package planner contains only answer file, manifest, payload, and installer-copy operations. It cannot include disk formatting, mount, boot-record, or USB-write operations. Tests cover compatible preview, incompatible edition block, and unknown-version warning.

Verified: `dotnet build Easyaller.slnx` with zero warnings and `dotnet test Easyaller.slnx --no-build` with 41 passing tests.

### [x] WP-021: Windows compatibility catalog

Model edition, architecture, display version, build range, supported settings, source links, and validation evidence. Start with Windows 11 Pro and Enterprise, amd64, 24H2 and 25H2.

Use `Documented`, `SchemaValidated`, `VmValidated`, `Warning`, and `Unsupported` as distinct states. Unknown builds must warn rather than use a workaround.

Implemented `Windows11CompatibilityCatalog` for documented Windows 11 Pro and Enterprise amd64 targets: 24H2 base build 26100 and 25H2 base build 26200. The catalog records supported setting categories, official source URI, and separate documented, schema, and VM evidence flags. Version and build outside this initial catalog warn rather than enable a workaround; edition and architecture conflicts block the preview. Public catalog notes are in `docs/COMPATIBILITY.md`.

Verified: `dotnet build Easyaller.slnx` with zero warnings and `dotnet test Easyaller.slnx --no-build` with 43 passing tests.

### [x] WP-022: Deployment profile validator

Validate locale, Windows time zone, architecture, target edition, OOBE combination, privacy state, and credentials. Block domain join and internal-disk partitioning in the deployment profile.

Required message for any credential field: `Deployment profile contains a forbidden credential field.`

Implemented `DeploymentProfileValidator` as the deployment boundary before a preview is created. It revalidates profile locale, time-zone, architecture, edition, OOBE consistency, and privacy preferences; evaluates the documented compatibility catalog; blocks profiles that require a domain join; excludes optional runtime domain prompts from the deployment preview; and scans serialized profiles for credential-like fields with the required message. Internal-disk partitioning has no deployment profile field or planned operation, so it cannot enter the file-only path. Added `docs/GETTING_STARTED.md` with the current launch, profile, safety, deployment-status, build, and test instructions.

Verified: `dotnet build Easyaller.slnx` with zero warnings and `dotnet test Easyaller.slnx --no-build` with 46 passing tests.

### [x] WP-023: Deterministic `autounattend.xml` generator

Implement `IUnattendGenerator` with an XML API. Generate only validated, explicitly configured settings: locale, time zone, documented OOBE settings, and the temporary local account from an ephemeral credential object.

Acceptance criteria:

- No string-built XML or shell injection.
- No product key, domain join, disk partitioning, or profile credential.
- Byte-identical output for identical inputs.
- XML escaping, validity, and prohibited-section tests.

Implemented `UnattendXmlGenerator` using `XmlWriter` with fixed namespace, component, pass, element, indentation, newline, and encoding rules. It writes documented locale settings in `windowsPE` and `oobeSystem`, time zone in `specialize`, and only explicitly configured OOBE settings. A temporary local account can be supplied only through `EphemeralLocalAccountCredential`, which redacts itself, zeroizes its password on disposal, and writes a Windows-SIM-style obfuscated password value with `PlainText=false`; account creation is omitted when no ephemeral object is supplied. The generator reuses deployment validation and refuses invalid profiles, required domain join, disposed account credentials, and unsupported account names. It never writes product keys, domain accounts, disk configuration, AutoLogon, or raw command sections. `FirstLogonCommands` remains absent unless the later explicit fixed bootstrapper option is validated.

Verified: `dotnet build Easyaller.slnx` with zero warnings and `dotnet test Easyaller.slnx --no-build` with 53 passing tests.

### [x] WP-024: Windows SIM validation harness

Document and automate answer-file validation against a supplied `install.wim` or `install.esd`. Record ISO hash, edition index, ADK/Windows SIM version, and validation result without committing image files.

Implemented the Windows-host-only `scripts/Validate-AnswerFile.ps1` harness and English/Russian validation guides. The harness requires an official ISO, exact `install.wim` or `install.esd`, image index, and generated answer file. It runs non-destructive XML policy checks and DISM image inspection, then records ISO/image/answer-file SHA-256 hashes, selected index, image metadata, Windows SIM result, version, and message in JSON outside the repository. Windows SIM validation remains a documented manual step with explicit `NotRun`, `Passed`, or `Failed` evidence, so the script cannot fabricate `SchemaValidated`. A static safety test protects the no-mount, no-format, no-diskpart boundary. No ISO, Windows image, catalog, or Windows SIM run was available in this macOS development environment.

Verified: `dotnet build Easyaller.slnx` with zero warnings and `dotnet test Easyaller.slnx --no-build` with 54 passing tests. The PowerShell harness is not executable in this macOS environment because PowerShell, DISM, Windows ADK, Windows SIM, and a supplied Windows image are unavailable.

### [x] WP-025: Temporary per-package local-account credential

Generate a cryptographically strong unique password only while producing a package. Show it once, never place it in profile storage, manifests, logs, exceptions, or test snapshots. AutoLogon remains out of the MVP.

Implemented `TemporaryLocalAccountCredentialFactory`. It uses `RandomNumberGenerator` to create a 24-character password with uppercase, lowercase, digit, and symbol categories, then returns `GeneratedTemporaryLocalAccount`. The generated object carries an ephemeral credential for XML generation and can reveal the password exactly once for a future package UI; both in-memory character arrays are zeroized when revealed or disposed, and both object string representations are redacted. The password never becomes a profile, manifest, log, exception, or test fixture value. AutoLogon remains absent from generated XML.

Verified: `dotnet build Easyaller.slnx` with zero warnings and `dotnet test Easyaller.slnx --no-build` with 57 passing tests.

### [x] WP-026: Preview and dry run

Show the effective profile, OOBE behavior, privacy state, compatibility status, and sensitive-material warning. Preview XML must use the same generator as export. Dry run changes neither disk nor Windows state.

Implemented `DeploymentDryRunService`. It first creates the file-only deployment preview, then calls the shared `IUnattendGenerator` and retains the exact answer-file bytes only in memory. Its result explicitly exposes the effective profile, OOBE settings, privacy settings, compatibility result, and sensitive-material warnings. Every dry run warns that profile data can be organization-specific; one with an ephemeral local account also warns that its obfuscated password is sensitive. No dry-run API accepts an output path or performs file, disk, or Windows-state writes.

Verified: `dotnet build Easyaller.slnx` with zero warnings and `dotnet test Easyaller.slnx --no-build` with 61 passing tests.

### [x] WP-027: Safe deployment-package export

Use staging plus atomic finalize to export `autounattend.xml`, manifest, local payload, scripts, selected profile, and allowed installers. Verify hashes. Do not call format, mount, diskpart, or boot-record operations.

Implemented `DeploymentPackageExporter`. It accepts a validated `DeploymentDryRun`, writes an answer file, selected profile, README, and SHA-256 manifest to a unique sibling staging directory, rereads every manifest entry for integrity verification, then atomically renames the directory to a previously nonexistent destination. Explicit payload, scripts, and installer assets must stay in their dedicated package directories; installer paths must match a `packageRelative` application declared by the selected profile. Absolute paths, traversal, duplicate targets, symbolic-link sources, and existing destinations are blocked. The manifest does not contain the temporary local-account password. The exporter performs ordinary file operations only and contains no disk, image, boot-record, or USB logic. English and Russian package-safety instructions are in `docs/DEPLOYMENT_PACKAGE.md` and `docs/DEPLOYMENT_PACKAGE_RU.md`.

Verified: `dotnet build Easyaller.slnx` with zero warnings and `dotnet test Easyaller.slnx --no-build` with 64 passing tests.

### [x] WP-028: Functional Prepare Windows 11 screen

Connect profile selection, validation, preview, dry run, and file-only package export. USB formatting must not appear in this screen yet.

Implemented the Russian **Set up this PC** deployment workflow. It selects a stored profile plus Windows 11 edition, display version, and build; shows compatibility preview, dry-run effective OOBE and privacy state, generated answer-file size, and sensitive-material warnings; then exports a basic package only after an explicit parent-folder choice. The screen clears a dry run when its profile or target changes and does not expose optional assets, temporary-account credentials, ISO handling, USB creation, or formatting. `DeploymentPreparationController` tests cover the documented target preview and basic package handoff.

Verified: `dotnet build Easyaller.slnx` with zero warnings and `dotnet test Easyaller.slnx --no-build` with 66 passing tests. The Avalonia window starts locally, but this macOS environment does not expose it to the available accessibility automation, so layout was verified by XAML compilation rather than an automated screenshot.

## Phase 3: first boot and privacy

### [x] WP-030: Configuration-set payload delivery

Deliver the local payload through `$OEM$\$1\ProgramData\Easyaller`, verify manifest hashes, and never overwrite Windows system files.

Implemented `ConfigurationSetPayloadLayout` and `ConfigurationSetPayloadVerifier`. Payload, scripts, and allowed installers now use the fixed `$OEM$/$1/ProgramData/Easyaller` configuration-set path. When a package contains payload assets, the exporter adds a `payload-manifest.json` inside that delivered directory, while the package-level manifest hashes the payload manifest itself. The read-only verifier rejects missing, malformed, unsafe, or changed delivered files by size and SHA-256 without any writes. No layout path can target a Windows system directory, and this task does not mount an image or invoke Windows Setup.

Verified: `dotnet build Easyaller.slnx` with zero warnings and `dotnet test Easyaller.slnx --no-build` with 68 passing tests.

### [x] WP-031: FirstLogon bootstrapper

After one manual `ProvisioningAdmin` sign-in, run a small idempotent bootstrapper through `FirstLogonCommands`. It verifies the local payload, activates resume, launches Easyaller, and removes its one-time launch mechanism after success.

Implemented the opt-in `FirstLogonBootstrapper`. It is valid only for an active temporary `ProvisioningAdmin` account, `FirstLogon` launch mode, and a package that includes `Easyaller.App.exe` below the verified configuration-set payload. It writes one fixed ordered `FirstLogonCommands/SynchronousCommand` and embeds a hash-verifying script into the payload. After the manual administrator sign-in, the initial invocation validates each payload file, writes the fixed `!EasyallerBootstrapResume` value under `HKLM\Software\Microsoft\Windows\CurrentVersion\RunOnce`, and starts `Easyaller.App.exe --resume` without blocking the desktop. The resume invocation never writes RunOnce. On the exact resume argument, Easyaller removes RunOnce when possible and records completed state after its main window is created. The script accepts no profile command, secret, domain input, or dynamic argument. `AutoLogon` remains absent. English and Russian safety notes are in `docs/FIRST_LOGON_BOOTSTRAP.md` and `docs/FIRST_LOGON_BOOTSTRAP_RU.md`.

Verified: `dotnet build Easyaller.slnx` with zero warnings and `dotnet test Easyaller.slnx --no-build` with 73 passing tests. The first-logon behavior was not executed on Windows in this macOS environment; Windows SIM and VM validation remain required.

### [x] WP-032: Temporary-account cleanup state machine

Track `created`, `firstLogin`, `provisioning`, `domainJoined`, `validated`, `cleanupEligible`, and `cleaned`. Delete or disable only when expected administrator access has been verified.

Implemented an immutable in-memory `TemporaryAccountCleanupStateMachine`. It accepts only ordered lifecycle events, skips `domainJoined` only when domain join is not required, and will not mark cleanup eligible until the required resume, required domain join, and expected administrator access are all verified. The selected profile policy returns a `disable` or `delete` plan only at `cleanupEligible`; no current code executes an operating-system account operation. English and Russian safety notes are in `docs/TEMPORARY_ACCOUNT_CLEANUP.md` and `docs/TEMPORARY_ACCOUNT_CLEANUP_RU.md`.

Verified: `dotnet build Easyaller.slnx` with zero warnings and `dotnet test Easyaller.slnx --no-build` with 77 passing tests. Windows SIM and VM validation are still required before a real account-management adapter can be added.

### [x] WP-033: PrivacyConfigurationService

Separate post-install privacy policies from OOBE page hiding. Apply only documented policies with a supported-build check and a verification method. `notConfigured` must remain a no-op.

Implemented an explicit `PrivacyConfigurationService` with a separately injectable Windows registry adapter. It accepts `PrivacySettings` and a Windows target only, so OOBE page hiding cannot enter the post-install policy path. On Windows 11 Pro or Enterprise amd64 build 26100 or later, it can apply and reread documented policies for location services, disabling advertising ID, and disabling online speech recognition. `notConfigured` and `userChoice` make no writes and do not remove existing organization policy. Unsupported fields and unsupported force-enable requests warn without writing an invented registry value. The desktop UI does not invoke the adapter. English and Russian policy notes are in `docs/PRIVACY_POLICIES.md` and `docs/PRIVACY_POLICIES_RU.md`.

Verified: `dotnet build Easyaller.slnx` with zero warnings and `dotnet test Easyaller.slnx --no-build` with 83 passing tests. The adapter was not invoked against Windows in this macOS environment; Windows SIM and VM validation are required before workstation use.

### [x] WP-034: Runtime Windows version gate

Detect edition, display version, build, and architecture after installation. Compare them with the package manifest and profile. Unknown builds warn and skip unvalidated actions.

Implemented a read-only `RuntimeWindowsVersionGate` and Windows-only runtime provider. It compares detected edition, architecture, display version, and build exactly with the deployment manifest, then verifies that the selected profile identity, revision, architecture, and supported edition agree. Any mismatch blocks validated actions. A missing detector result or build outside the documented compatibility catalog produces a warning and skips validated actions without a workaround. The desktop UI does not invoke the gate yet. English and Russian notes are in `docs/RUNTIME_VERSION_GATE.md` and `docs/RUNTIME_VERSION_GATE_RU.md`.

Verified: `dotnet build Easyaller.slnx` with zero warnings and `dotnet test Easyaller.slnx --no-build` with 88 passing tests. The provider was not executed against Windows in this macOS environment; Windows SIM and VM validation remain required.

## Phase 4: VM validation

### [x] WP-040: VM test guide and fixtures

Create `docs/VM_TESTING.md` with VM prerequisites, snapshots, official ISO handling, manual disk choice, mock domain join, evidence collection, and test-secret cleanup.

Added English and Russian VM test guides plus a safe committed evidence template. The guides require an isolated Windows 11 VM with documented UEFI, Secure Boot, vTPM, storage, memory, and CPU settings; a `before-oobe` checkpoint; administrator-supplied official ISO; manual choice of one empty VM disk; mock-only domain results; evidence outside Git; and test-secret cleanup. They prohibit physical disks, USB pass-through, production domains, production credentials, completed evidence, VM disks, snapshots, and generated passwords in the repository. Added ignore rules for VM disk-chain artifacts and local VM evidence.

Verified: `dotnet build Easyaller.slnx` with zero warnings and `dotnet test Easyaller.slnx --no-build` with 88 passing tests. No VM or Windows ISO is available in this macOS environment, so no scenario has been marked `VmValidated`.

### [ ] WP-041: VM matrix

Run the complete flow for Windows 11 24H2 and 25H2, Pro and Enterprise: offline OOBE behavior, locale, temporary sign-in, local payload, name input, mock network/domain/reboot/resume, cleanup, corrupted payload, and unknown-build warning.

Skipped for now by the user. Resume only when an approved Windows 11 Pro or Enterprise amd64 VM, official ISO, and checkpoint-capable hypervisor are available. Do not mark any catalog entry `VmValidated` without the exact evidence described in `docs/VM_TESTING.md`.

## Phase 5: protected USB creation

### [x] WP-050: Read-only disk inventory and safety model

Use immutable disk identity, removable status, bus type, serial, size, system-disk and boot-disk checks. Never choose a default target. Add a mock provider and hot-swap tests.

Implemented `WindowsRemovableDiskInventoryProvider` and `RemovableDiskSafetyService`. The fixed Windows-only query reads disk facts only: number, vendor, friendly name, serial, immutable `UniqueId`, bus, removable state, size, and system, boot, read-only, and offline flags. The safety service never selects a default target and permits explicit confirmation only for a uniquely identified removable USB, SD, or MMC disk that is writable, online, non-system, non-boot, and positive-sized. The selection is rechecked by immutable identity and serial before future operations, so a replacement at the same disk number is blocked while a safe renumbering of the same device remains valid. English and Russian notes are in `docs/DISK_SAFETY.md` and `docs/DISK_SAFETY_RU.md`.

Verified: `dotnet build Easyaller.slnx` with zero warnings and `dotnet test Easyaller.slnx --no-build` with 93 passing tests. The Windows provider was not invoked in this macOS environment, and no destructive disk operation exists.

### [x] WP-051: Read-only ISO inspection

Validate the supplied ISO structure, image editions, hash, architecture, and file-size constraints before any format operation. Always unmount safely.

Implemented `WindowsIsoInspectionService` with a 12 GiB default limit, absolute-local-path and extension checks, file-size checks, and a SHA-256 result before Windows accesses the ISO. Its Windows-only content reader uses a fixed non-interactive PowerShell query that mounts only with `Mount-DiskImage -Access ReadOnly`, reads required setup paths and `Get-WindowsImage` metadata, and calls `Dismount-DiskImage` in `finally`. It blocks missing setup structure, zero or ambiguous install images, non-amd64 images, and a missing selected Professional or Enterprise amd64 edition. It is not connected to the desktop UI or any disk-writing flow. English and Russian notes are in `docs/ISO_INSPECTION.md` and `docs/ISO_INSPECTION_RU.md`.

Verified: `dotnet build Easyaller.slnx` with zero warnings and `dotnet test Easyaller.slnx --no-build` with 98 passing tests. A real ISO was not mounted in this macOS environment; the fixed Windows probe is covered by contract tests and must still be exercised on a disposable Windows host before it is treated as an operational USB workflow.

### [x] WP-052: Destructive confirmation state machine

Show vendor, serial or device ID, disk number, and size. Require `ERASE`. Bind the short-lived confirmation to immutable device identity and recheck before the first write.

Implemented `UsbDestructiveConfirmationStateMachine`. It creates an in-memory five-minute prompt only for an explicitly selected eligible removable disk, showing vendor, serial or immutable device ID, disk number, and byte size. It accepts only the exact uppercase `ERASE` phrase. Immediately before a future first write, `AuthorizeFirstWrite` rechecks immutable ID, optional serial number, and current disk safety, then consumes the authorization once. Expired, replayed, unconfirmed, hot-swapped, duplicated, missing, or newly unsafe selections remain blocked. There is still no desktop UI or disk-writing engine. English and Russian notes are in `docs/USB_CONFIRMATION.md` and `docs/USB_CONFIRMATION_RU.md`.

Verified: `dotnet build Easyaller.slnx` with zero warnings and `dotnet test Easyaller.slnx --no-build` with 103 passing tests. The state machine uses mock inventory only; no disk action exists or was invoked.

### [x] WP-053: USB write engine

Separate planning from execution. Copy Setup files and deployment additions only after safety checks. Verify output hashes and never report a partially written USB as ready.

Implemented `UsbMediaWriteEngine`. It builds an immutable plan with source paths, destination paths, byte lengths, and SHA-256 values from a setup-media directory and verified deployment package, then rejects unsafe sources, missing answer files, changed package files, and destination collisions. Execution requires the consumed one-time authorization, checks every source again before opening the target, writes only through an explicit `IUsbMediaWriteTarget`, commits only after all writes, and verifies every final hash before returning ready. A changed source stops before copying begins; a failed or partial target is never reported as ready. There is intentionally no built-in Windows volume adapter yet, so the app cannot write a physical USB or arbitrary filesystem path. English and Russian notes are in `docs/USB_WRITE_ENGINE.md` and `docs/USB_WRITE_ENGINE_RU.md`.

Verified: `dotnet build Easyaller.slnx` with zero warnings and `dotnet test Easyaller.slnx --no-build` with 107 passing tests. Only an in-memory target was invoked; no disk or filesystem target was written.

Added a Windows-only `WindowsUsbVolumeBindingInspector` and `DiskBoundDirectoryUsbMediaWriteTarget`. The fixed read-only query binds a requested volume root to a single partition and disk through `Get-Volume`, `Get-Partition`, `Get-Disk`, and `Win32_DiskDrive`; it then reuses immutable ID, serial, removable, bus, system, boot, read-only, and offline safety checks. The target rejects any nonempty root, stages files below `.easyaller-staging-*`, checks for root changes before commit, and verifies final hashes through the write engine. It is not connected to UI and has not been run against a physical USB. English and Russian notes are in `docs/USB_VOLUME_BINDING.md` and `docs/USB_VOLUME_BINDING_RU.md`.

Verified: `dotnet build Easyaller.slnx` with zero warnings and `dotnet test Easyaller.slnx --no-build` with 111 passing tests. The Windows query was checked by script-contract tests, while the staging target was exercised only against a temporary test directory with a mock binding inspector.

### [x] WP-054: Create installation USB screen

Add the functional UI only after the safety engine and mock-disk tests pass. The normal UI must never expose a system or fixed internal disk as a target.

Implemented the Russian desktop window **Создать установочный USB** and `UsbCreationController`. The screen obtains read-only inventory and shows only eligible removable candidates, never selecting one by default or exposing blocked system and fixed disks as targets. It picks Windows Setup and deployment-package directories, prepares a hash-verified plan, displays vendor, serial or immutable ID, disk number, and size, requires exact `ERASE`, refreshes inventory, resolves exactly one volume root for the authorized disk, and sends it through disk-bound staging and final hash verification. A nonempty root, changed disk, ambiguous volume, changed source, or failed hash stops without ready status. English and Russian notes are in `docs/USB_CREATOR.md` and `docs/USB_CREATOR_RU.md`.

Verified: `dotnet build Easyaller.slnx` with zero warnings and `dotnet test Easyaller.slnx --no-build` with 115 passing tests. The desktop XAML compiled, and controller, binding, and write flows were tested with mocks and temporary directories. No physical USB was written in this macOS environment.

## Phase 6: public release and pilot

### [x] WP-060: Deployment documentation

Document answer-file discovery, OOBE limits, manual disk choice, temporary account, first boot, cleanup, logs, compatibility matrix, security warnings, and troubleshooting.

Added one English and Russian operator guide that connects the existing package, USB, Windows SIM, VM, compatibility, first-logon, and cleanup documents into a single controlled test path. It records the implemented USB-copy workflow and every pre-alpha limitation: preformatted empty test media only, manual internal-disk selection, no ISO mutation, no domain join, no documented OOBE workaround bypasses, no UI temporary account or bootstrapper, and no physical-media success claim. The guide specifies root-level `autounattend.xml` discovery, Panther log collection and redaction, required evidence, current documented-only compatibility status, stop conditions, and conservative troubleshooting. `PRODUCT_SPEC.md`, the README, and both getting-started guides now link the runbook and state that USB copying is implemented but physical validation remains pending.

Verified: `dotnet build Easyaller.slnx` with zero warnings and `dotnet test Easyaller.slnx --no-build` with 115 passing tests.

### [x] WP-061: Public repository readiness

Keep contributor-facing public text in English while retaining the user-requested Russian desktop UI. Add contribution guidance, security policy, CI, neutral examples, and a chosen license. Scan current files and history for secrets and organization data. Do not publish a release before the owner explicitly authorizes it.

Added a least-privilege GitHub Actions CI workflow for restore, Release build, and tests on pushes to `main` and pull requests. Added English `CONTRIBUTING.md` and `SECURITY.md`, including public-profile boundaries, local verification, private vulnerability reporting, and safe research limits. Added a neutral public profile example and a unit test that parses it, so the example cannot silently drift from the profile contract. The repository already contains an MIT license. A current-tree scan and a scan across all 33 commits found no high-confidence credential or private-key material; expected secret-related source and test literals were manually reviewed. No release was created. The Russian desktop UI remains intentional; contributor-facing material is English unless it is an explicit Russian translation.

Verified: Debug and Release `dotnet build Easyaller.slnx` completed with zero warnings; Debug and Release `dotnet test Easyaller.slnx --no-build` completed with 116 passing tests.

### [ ] WP-062: Physical-PC pilot

Use only a dedicated test PC and recoverable USB. Record firmware mode, hardware, ISO hash, edition/build, and actual results. Do not use corporate credentials until explicitly authorized.

Skipped for now by the user. Resume only with a dedicated non-production PC, a recoverable test USB, and recorded evidence outside Git.

## Blocked inputs

### [x] WP-070: Classify approved organization instructions

Reviewed the approved local setup instructions and created an ignored local assessment that classifies every instruction as automated, runtime-validated, manual, or unsupported. Organization-specific domain, network, proxy, installer-source, access-right, and credential values remain outside the repository, public examples, and defaults. The initial assessment identified static IPv4/DNS, proxy bypass rules, and application installation as separate generic modules; the IPv4/DNS module is now recorded in WP-072, while proxy bypass rules and application installation remain separate work.

### [x] WP-072: Generic static IPv4 and DNS profiles

Added the optional `staticIpv4` profile block for a validated IPv4 address, subnet mask, default gateway, and one to three DNS servers. The profile editor can create and edit it, and export preview marks it confidential. Existing runtime profiles remain compatible. Applying the plan still requires an explicit enabled adapter name or interface GUID and exact `APPLY`; the Windows adapter changes only that adapter, verifies the resulting address and DNS, and attempts a same-adapter rollback on failure. No profile stores credentials, Wi-Fi, VPN, or arbitrary commands. Mock tests cover validation, serialization, planning, editor persistence, operation order, and adapter binding. Real Windows VM validation remains mandatory before workstation use.

### [x] WP-071: Publish the first GitHub prerelease

The owner explicitly authorized the public MIT-licensed GitHub prerelease `v0.1.0` on 2026-08-09. The tag targets commit `9788ff4`, whose GitHub Actions CI run passed. It contains source archives and English release notes only, with no local profiles, deployment packages, credentials, or installers. This is a pre-alpha testing milestone, not a claim of Windows VM or physical-media validation.

### [x] WP-073: Generic WinHTTP proxy bypass profiles

Added the optional `machine.proxy.bypassList` profile field for up to 256 validated host, IPv4, wildcard, or `<local>` entries. It is available only with `promptAtRuntime` proxy mode, is marked confidential before export, and never accepts URLs, whitespace, arbitrary command text, or duplicate entries. The Russian profile editor stores comma, semicolon, or newline-separated values. On an exact `APPLY`, the Windows adapter supplies the saved list only to the fixed `Set-WinhttpProxy` operation after the runtime proxy address is validated. The scope is WinHTTP only, not all browsers or applications. No proxy address, credential, or local configuration is committed. Mock tests cover validation, planning, profile editing, export review, and execution binding. Windows VM validation remains mandatory before use on a workstation.

### [x] WP-074: Task-oriented Russian interface

Reorganized the profile editor from a single technical field list into four ordered stages: Windows and first launch, network and access, domain and setup launch, then applications and manual instructions. Every high-impact selection now explains its practical result, scope, and what is intentionally not stored. The apply screen now separates three actions: safe plan review, file-only Windows 11 package preparation, and confirmed changes to the current PC. Its labels clarify which values are runtime-only and that validation does not modify Windows. This is a content and layout improvement only: profile data, validation, and operation boundaries remain unchanged.
