# Runtime Windows version gate

Before any future first-boot action uses a deployment package, Easyaller can read the installed Windows edition, display version, build, and architecture, then compare them with both the deployment manifest and selected profile.

The Windows provider is read-only. It reads `EditionID`, `DisplayVersion`, and `CurrentBuildNumber` from `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion` and uses the runtime OS architecture. On another operating system or when the values are unavailable, it returns a warning and no validated action is allowed.

| State | Meaning | Validated actions |
| --- | --- | --- |
| `ready` | Runtime, manifest, profile, and documented catalog match | Allowed |
| `warning` | Runtime is unavailable or the build is outside the catalog | Skipped |
| `blocked` | Runtime does not match the manifest, or manifest and profile disagree | Blocked |

The gate requires an exact match for edition, architecture, display version, and build. Unknown editions, builds, and architectures do not trigger a workaround. They remain warnings or blocks until the compatibility catalog, Windows SIM evidence, and VM validation have been deliberately updated.

The current desktop screen does not invoke this gate yet. It is a read-only component for the future first-boot execution flow. See [Get-ComputerInfo](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.management/get-computerinfo) and [Windows 11 release information](https://learn.microsoft.com/en-us/windows/release-health/windows11-release-information).
