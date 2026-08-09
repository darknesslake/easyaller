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

### [ ] WP-015: Connect a selected profile to the provisioning pipeline

Map a selected profile into the existing setup pipeline without creating a second engine. Collect final computer name, adapter choice, network values, and credentials only at runtime.

Acceptance criteria:

- `notConfigured` values perform no system change.
- Credentials are short-lived in memory and redacted in logs.
- Reboot and resume behavior has integration coverage.

Implemented the shared `ProvisioningPlan` contract and builder, plus the Set up this PC desktop screen. A valid profile maps to declarative steps and explicit runtime prompts for computer name, adapter and network choice, proxy, and domain join. The screen previews the same plan and validates runtime input without changing Windows. `notConfigured` preferences produce no system-change step. Runtime domain credentials are an in-memory disposable object with redacted string output and the password field is cleared after validation. The task remains open until an executor applies the plan with reboot and resume coverage.

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

Implemented `UnattendXmlGenerator` using `XmlWriter` with fixed namespace, component, pass, element, indentation, newline, and encoding rules. It writes documented locale settings in `windowsPE` and `oobeSystem`, time zone in `specialize`, and only explicitly configured OOBE settings. A temporary local account can be supplied only through `EphemeralLocalAccountCredential`, which redacts itself, zeroizes its password on disposal, and writes a Windows-SIM-style obfuscated password value with `PlainText=false`; account creation is omitted when no ephemeral object is supplied. The generator reuses deployment validation and refuses invalid profiles, required domain join, disposed account credentials, and unsupported account names. It never writes product keys, domain accounts, disk configuration, AutoLogon, FirstLogonCommands, or raw command sections.

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

### [ ] WP-027: Safe deployment-package export

Use staging plus atomic finalize to export `autounattend.xml`, manifest, local payload, scripts, selected profile, and allowed installers. Verify hashes. Do not call format, mount, diskpart, or boot-record operations.

### [ ] WP-028: Functional Prepare Windows 11 screen

Connect profile selection, validation, preview, dry run, and file-only package export. USB formatting must not appear in this screen yet.

## Phase 3: first boot and privacy

### [ ] WP-030: Configuration-set payload delivery

Deliver the local payload through `$OEM$\$1\ProgramData\Easyaller`, verify manifest hashes, and never overwrite Windows system files.

### [ ] WP-031: FirstLogon bootstrapper

After one manual `ProvisioningAdmin` sign-in, run a small idempotent bootstrapper through `FirstLogonCommands`. It verifies the local payload, activates resume, launches Easyaller, and removes its one-time launch mechanism after success.

### [ ] WP-032: Temporary-account cleanup state machine

Track `created`, `firstLogin`, `provisioning`, `domainJoined`, `validated`, `cleanupEligible`, and `cleaned`. Delete or disable only when expected administrator access has been verified.

### [ ] WP-033: PrivacyConfigurationService

Separate post-install privacy policies from OOBE page hiding. Apply only documented policies with a supported-build check and a verification method. `notConfigured` must remain a no-op.

### [ ] WP-034: Runtime Windows version gate

Detect edition, display version, build, and architecture after installation. Compare them with the package manifest and profile. Unknown builds warn and skip unvalidated actions.

## Phase 4: VM validation

### [ ] WP-040: VM test guide and fixtures

Create `docs/VM_TESTING.md` with VM prerequisites, snapshots, official ISO handling, manual disk choice, mock domain join, evidence collection, and test-secret cleanup.

### [ ] WP-041: VM matrix

Run the complete flow for Windows 11 24H2 and 25H2, Pro and Enterprise: offline OOBE behavior, locale, temporary sign-in, local payload, name input, mock network/domain/reboot/resume, cleanup, corrupted payload, and unknown-build warning.

## Phase 5: protected USB creation

### [ ] WP-050: Read-only disk inventory and safety model

Use immutable disk identity, removable status, bus type, serial, size, system-disk and boot-disk checks. Never choose a default target. Add a mock provider and hot-swap tests.

### [ ] WP-051: Read-only ISO inspection

Validate the supplied ISO structure, image editions, hash, architecture, and file-size constraints before any format operation. Always unmount safely.

### [ ] WP-052: Destructive confirmation state machine

Show vendor, serial or device ID, disk number, and size. Require `ERASE`. Bind the short-lived confirmation to immutable device identity and recheck before the first write.

### [ ] WP-053: USB write engine

Separate planning from execution. Copy Setup files and deployment additions only after safety checks. Verify output hashes and never report a partially written USB as ready.

### [ ] WP-054: Create installation USB screen

Add the functional UI only after the safety engine and mock-disk tests pass. The normal UI must never expose a system or fixed internal disk as a target.

## Phase 6: public release and pilot

### [ ] WP-060: Deployment documentation

Document answer-file discovery, OOBE limits, manual disk choice, temporary account, first boot, cleanup, logs, compatibility matrix, security warnings, and troubleshooting.

### [ ] WP-061: Public repository readiness

Keep all public text and UI in English. Add contribution guidance, security policy, CI, neutral examples, and a chosen license. Scan current files and history for secrets and organization data. Do not publish a release before the owner chooses a license.

### [ ] WP-062: Physical-PC pilot

Use only a dedicated test PC and recoverable USB. Record firmware mode, hardware, ISO hash, edition/build, and actual results. Do not use corporate credentials until explicitly authorized.

## Blocked inputs

### [ ] WP-070: Apply approved organization instructions

Wait for an approved manual setup document. Classify every instruction as automated, manual, validated, or unsupported. Never invent domain, proxy, network, privacy, installer, or security values.

### [ ] WP-071: Choose license and publish the first release

The owner must choose the repository license, visibility, and release policy. Creating or pushing a GitHub repository is authorized, but publishing a release requires the license decision.
