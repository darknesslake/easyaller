# Getting started with Easyaller

Easyaller is a Windows 11 workstation provisioning and maintenance application. It stores repeatable standards in profiles, applies only explicitly selected operations, installs applications in order, and provides separate maintenance tools for configured PCs.

## Run from source

.NET SDK 10 is required:

```powershell
dotnet run --project src/Easyaller.App/Easyaller.App.csproj
```

Verify the project with:

```powershell
dotnet build Easyaller.slnx
dotnet test Easyaller.slnx --no-build
```

## Start screen and profiles

Easyaller always opens on the mode-selection screen. Select a profile before entering a mode. This screen is the only place where profiles are selected, created, cloned, renamed, described, deleted, imported, or exported. Profiles never store passwords.

Choose one of three modes:

- **Set up this computer** for an already installed Windows workstation.
- **PC maintenance** for standalone shortcut and Outlook operations.
- **New USB Install** for a new-Windows deployment package.

Use **Exit mode** in the upper-left corner to return to the start screen and select another profile or mode.

## Selective application

Open **Set up this computer** and select **Apply settings** in the left navigation. The left side contains section navigation only; profile selection stays on the start screen. Each action button applies only its corresponding setting. Blank runtime fields do not clear existing Windows values, and saving a profile does not apply it. Easyaller shows an explicit confirmation and exact operation list before changing Windows; system operations require administrator rights.

## Application queue

Use the **↑** and **↓** buttons on each application row to set the order. Installers are copied locally and run sequentially. A failed queue can retry the failed installer, skip it and continue, stop the active process, or resume after an Easyaller restart. Recent-run history includes outcomes, exit codes, and the problem application.

## PC maintenance

Maintenance operations are separate from new-installation profiles.

- Copy `.lnk`, `.url`, and `.website` shortcuts to a selected Windows user's real Desktop, localized desktop, or OneDrive Desktop after an access and conflict preview.
- Archive classic Outlook Inbox and Sent Items mail into a dated PST with age filters, progress, cancellation, verification, error details, duplicate-run protection, and history.

New Outlook is not supported by the archive workflow.

## New Windows preparation

**New USB Install** creates a validated deployment package and a protected USB layout. Verify the ISO, target disk, and operation summary, then test first in a VM or on dedicated removable media.

See [provisioning execution](PROVISIONING_EXECUTION.md), [deployment operations](DEPLOYMENT_OPERATIONS.md), and [disk safety](DISK_SAFETY.md).
