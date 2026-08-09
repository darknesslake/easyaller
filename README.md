# Easyaller

Easyaller is an open-source Windows workstation provisioning tool. It separates reusable, versioned configuration profiles from sensitive per-machine deployment packages, so teams can share setup standards without committing credentials or local configuration to Git.

> Status: pre-alpha. The profile contract, validation foundation, local profile storage, import/export service, first functional desktop screen, and deterministic answer-file generator are implemented. Deployment-package export, Windows SIM validation, first-boot orchestration, and USB creation are not implemented yet.

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
- Set up this PC screen that previews the selected profile plan and validates runtime-only computer, network, proxy, and domain input without applying it yet.
- Deployment module contracts for compatibility validation, answer-file generation, preview, dry run, package planning, and file-only export.
- Deterministic, XML-writer-based `autounattend.xml` generation for validated locale, time-zone, explicitly configured OOBE values, and an optional ephemeral local account. The desktop UI and package export do not expose it yet.
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

## License

[MIT](LICENSE)
