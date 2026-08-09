# Easyaller product specification

Status: working specification for incremental development.

## Product direction

Easyaller is a general-purpose, open-source application for preparing and configuring Windows workstations. It is not tied to a single organization, network, domain, or installer set.

The product has four major modes:

1. **Set up this PC** - computer name, network, proxy, domain join, reboot, applications, instructions, and final validation.
2. **Profiles** - create, edit, clone, validate, import, and export workstation profiles.
3. **Prepare Windows 11** - create and review `autounattend.xml`, run a dry run, and export a deployment package.
4. **Create installation USB** - a separately protected copy workflow for an already empty, preformatted removable volume. Physical-media validation remains pending.

The initial target is Windows 11 Pro and Enterprise on amd64. Windows Home, Windows in S mode, ISO downloading, automatic internal-disk partitioning, and fleet management are not part of the first release.

## Non-negotiable boundaries

- Use documented Windows Setup, Unattend, policy, and configuration-set mechanisms.
- Do not build on `BYPASSNRO`, `SkipMachineOOBE`, `SkipUserOOBE`, UI automation, SendKeys, or undocumented OOBE workarounds.
- Do not claim offline OOBE support for an ISO or build until Windows SIM and VM validation have passed.
- Do not put domain join, domain credentials, passwords, or tokens in reusable profiles, source control, logs, or `autounattend.xml`.
- Do not download Windows ISO files. An administrator supplies an official ISO.
- Do not select a disk automatically. USB formatting requires independent checks and explicit entry of `ERASE`.
- Keep internal Windows disk selection manual in the MVP.
- Deployment must hand off to the same provisioning pipeline used by the running application.

## Reusable profile versus deployment package

### Reusable profile

The public, Git-friendly format is `*.wpprofile.json`.

- `schemaVersion`, stable `profileId`, and `revision` identify and evolve a profile.
- The profile is declarative. It must not contain raw shell commands or credentials.
- Domain credentials are represented only by `promptAtRuntime`.
- A profile may contain confidential but non-secret information such as an internal domain name or proxy address. Export must show those fields for review.
- Imported profiles are untrusted until schema validation, semantic validation, and user review have completed.
- The profile contract is [`schemas/provisioning-profile.schema.json`](schemas/provisioning-profile.schema.json).

### Deployment package

A deployment package is generated for one installation. It is sensitive and must never be committed to Git.

```text
WindowsDeployment/
  autounattend.xml
  deployment-manifest.json
  sources/
    $OEM$/
      $1/
        ProgramData/
          Easyaller/
            Easyaller.exe
            profiles/
            scripts/
            installers/
  README.txt
```

The manifest records the app version, profile hash, target build, and file hashes. It does not contain credentials.

## Profile import and export

Import must:

1. Enforce a small file-size limit and valid UTF-8.
2. Reject duplicate JSON keys, unknown fields, and unknown future major schema versions.
3. Apply JSON Schema and semantic validation.
4. Apply explicit, non-destructive migrations for older versions.
5. Scan for secret-like fields, unsafe paths, and raw commands.
6. Show a preview and diff before writing any local file.
7. Offer `Create copy` or `Replace profile` when the `profileId` already exists.
8. Save atomically after explicit confirmation.

Export must use deterministic UTF-8 JSON with a stable property order and a final newline. It must show confidential fields before writing the file.

## Secrets and the first local sign-in

Reusable profiles contain markers only: `promptAtRuntime`, `generatePerPackage`, and `notConfigured`.

The MVP first-boot flow is:

1. Generate a cryptographically strong password for the per-machine `ProvisioningAdmin` account.
2. Place it only in the generated answer file when Windows Setup needs it.
3. Show it once to the administrator and never log or save it in a profile or manifest.
4. Require one manual local sign-in. AutoLogon is not enabled by default.
5. Copy the Easyaller payload locally through a configuration set.
6. Use `FirstLogonCommands` only to start a small, idempotent bootstrapper.
7. Disable or delete the temporary account only after domain join, resume, and final validation prove that administrative access remains available.

`PlainText=false` in an answer file is hiding, not encryption. The generated package requires physical protection.

## Windows Setup and OOBE

The answer-file generator may use only validated settings such as locale, time zone, local user creation, `HideEULAPage`, `HideOEMRegistrationScreen`, `HideOnlineAccountScreens`, `HideWirelessSetupInOOBE`, and `ProtectYourPC`.

It must generate XML through an XML API, include only explicitly configured supported settings, escape all values, and produce deterministic output. The MVP does not generate internal-disk partitioning, product keys, domain join, or guessed privacy defaults.

Each supported target records edition, display version, build range, ISO hash, ADK/Windows SIM version, schema-validation result, and VM-test result. Start with Windows 11 24H2 and 25H2. Unknown builds display a warning instead of enabling a workaround.

`SetupComplete.cmd` is not the primary mechanism because it has documented restrictions and does not verify script exit codes. It may become an optional adapter only for verified Enterprise scenarios.

## Privacy, applications, and instructions

Hiding privacy screens is separate from applying real privacy policies. `PrivacyConfigurationService` must use documented policy-backed mechanisms and preserve `notConfigured` as a no-op.

Application profiles are declarative: ID, display name, source, package-relative path, hash, arguments array, exit codes, and detection rule. Imported profiles cannot contain raw shell strings, absolute paths, or traversal paths. Proprietary installers do not belong in the public repository.

Instructions are data, not code. Render Markdown or text safely without executing HTML or scripts.

## Definition of done for the first safe release

- Profiles create, import, export, and validate without storing credentials.
- The existing configure-this-PC flow runs from a selected profile.
- `autounattend.xml` is deterministic and Windows SIM validated for each target.
- Deployment export creates files only and never formats a disk.
- First boot starts Easyaller after one manual temporary-account sign-in.
- Cleanup happens only after final validation.
- Supported Pro and Enterprise VM scenarios pass for every claimed Windows build.
- USB creation uses an explicit protected workflow, but no physical-media result is claimed until removable-drive protections and the exact media flow pass their own test suite.

## Primary Microsoft references

- [Automate OOBE](https://learn.microsoft.com/en-us/windows-hardware/customize/desktop/automate-oobe?view=windows-11)
- [Windows Setup Automation Overview](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/windows-setup-automation-overview?view=windows-11)
- [Answer-file authoring best practices](https://learn.microsoft.com/en-us/windows-hardware/customize/desktop/wsim/best-practices-for-authoring-answer-files)
- [Hide sensitive data in an answer file](https://learn.microsoft.com/en-us/windows-hardware/customize/desktop/wsim/hide-sensitive-data-in-an-answer-file)
- [Distribution shares and configuration sets](https://learn.microsoft.com/en-us/windows-hardware/customize/desktop/wsim/distribution-shares-and-configuration-sets-overview)
- [FirstLogonCommands](https://learn.microsoft.com/en-us/windows-hardware/customize/desktop/unattend/microsoft-windows-shell-setup-firstlogoncommands)
- [Add a custom script to Windows Setup](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/add-a-custom-script-to-windows-setup?view=windows-11)
- [Privacy Policy CSP](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-privacy)
