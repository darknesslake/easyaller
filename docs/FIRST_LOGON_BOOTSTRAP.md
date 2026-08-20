# First-logon bootstrapper

The bootstrapper is an explicit deployment API option. The desktop application does not enable it yet.

## Preconditions

All of the following are required before an answer file can contain `FirstLogonCommands`:

- the request explicitly enables the first-logon bootstrapper;
- the profile uses the `FirstLogon` launch mode;
- the active temporary local account is exactly `ProvisioningAdmin`;
- the package includes a regular-file application payload at `$OEM$/$1/ProgramData/Easyaller/payload/Easyaller.exe`.

The generator emits one fixed, ordered `SynchronousCommand` in the `oobeSystem` pass. It points only to `C:\ProgramData\Easyaller\scripts\Start-EasyallerBootstrap.ps1`. Reusable profiles cannot provide a command line, script text, account name, password, domain value, or arguments. AutoLogon remains absent.

## Bootstrap flow

After a manual administrator sign-in, the script verifies every payload-manifest hash before using the payload and requires an entry for the packaged Easyaller application. In its initial mode only, it writes the exact fixed command to `HKLM\Software\Microsoft\Windows\CurrentVersion\RunOnce` as `!EasyallerBootstrapResume`, then launches `Easyaller.exe --resume` without waiting for the GUI process. The resume invocation does not write to `RunOnce`. Once Easyaller has constructed its main window with the exact resume argument, it removes the entry and saves local completed state.

The `!` prefix asks Windows to defer deleting the `RunOnce` value until the bootstrap command returns. This is a one-time continuation mechanism, not a durable retry scheduler: if the bootstrap command itself completes but the GUI exits later, Windows may already have removed the entry. Easyaller therefore treats a missing entry as normal and never recreates it. See [Microsoft's RunOnce reference](https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys).

The command uses a process-scoped PowerShell execution-policy bypass only for this embedded, manifest-verified script. It does not accept user-provided PowerShell, download code, change the system execution policy, or run before payload verification.

Windows runs `FirstLogonCommands` the first time an administrator logs on, after logon and before the desktop is shown. The package path and answer file must still be validated in Windows SIM and a disposable VM before real use. See [Microsoft's FirstLogonCommands reference](https://learn.microsoft.com/en-us/windows-hardware/customize/desktop/unattend/microsoft-windows-shell-setup-firstlogoncommands).
