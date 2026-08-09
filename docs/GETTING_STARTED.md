# Getting started with Easyaller

Easyaller is pre-alpha software. It can safely create, edit, validate, import, and export reusable workstation profiles. The source code can generate a validated `autounattend.xml`, but the desktop application does not expose that workflow or export a deployment package yet. It does not change Windows settings or create a USB installer.

Русская версия: [GETTING_STARTED_RU.md](GETTING_STARTED_RU.md).

## Run the desktop application

Install the .NET 10 SDK, then clone the repository and start the Avalonia desktop application:

```sh
git clone https://github.com/darknesslake/easyaller.git
cd easyaller
dotnet run --project src/Easyaller.App/Easyaller.App.csproj
```

On this repository's macOS development environment, the SDK command is:

```sh
/opt/homebrew/opt/dotnet/libexec/dotnet run --project src/Easyaller.App/Easyaller.App.csproj
```

## Create and reuse a profile

1. Open the **Profiles** screen and select **New profile**.
2. Set the profile name, Windows edition and architecture, locales, time zone, OOBE preferences, privacy preferences, and machine prompts.
3. Add applications only as package-relative paths or external manual items. Do not add absolute paths, traversal paths, raw commands, passwords, tokens, or domain credentials.
4. Select **Save**. Easyaller validates the profile before writing it locally.
5. Use **Export** to create a portable `*.wpprofile.json` file. Review the confidential-field summary and confirm the export.
6. On another PC, use **Import**, review the result, and explicitly choose what to do if a profile with the same ID already exists.

Profiles are stored outside the repository: `%ProgramData%\Easyaller\Profiles` on Windows and the current user's local application-data directory on other supported desktop platforms. Local profiles and exported profile files are ignored by Git by default.

## Preview the future setup flow

Open **Set up this PC** to inspect the selected profile's provisioning plan. This screen can validate runtime-only values such as the computer name, network choice, proxy, and domain join data. It does not apply those values to Windows yet.

Domain credentials are intentionally collected only at runtime. They are not profile fields and must never be placed in an exported profile.

## Deployment status

The repository includes a documented Windows 11 compatibility catalog, a deployment-profile validator, and a deterministic `autounattend.xml` generator. They are preparation work for a future file-only deployment package. The generator allows only validated locale, time-zone, explicitly configured OOBE values, and an optional ephemeral local account. Its 24-character password is generated cryptographically, can be revealed once, and its internal character buffers are cleared after reveal or disposal. It does not generate disk configuration, product keys, domain joins, or AutoLogon.

The following are not implemented yet:

- Generating `autounattend.xml` from the desktop UI
- Exporting deployment files or a complete package
- Downloading or modifying ISO images
- Formatting disks or creating USB installers
- Joining a domain during Windows Setup

See [COMPATIBILITY.md](COMPATIBILITY.md) for the currently documented Windows targets and [PRODUCT_SPEC.md](../PRODUCT_SPEC.md) for product boundaries.

## Build and test

```sh
dotnet build Easyaller.slnx
dotnet test Easyaller.slnx --no-build
```

Run these commands before contributing a change. The test suite checks strict profile loading, local repository behavior, import and export safety, desktop editor logic, provisioning-plan construction, and deployment-profile validation.
