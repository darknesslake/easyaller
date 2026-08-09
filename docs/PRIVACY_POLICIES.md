# Post-install privacy policies

Easyaller separates Windows Setup OOBE page settings from post-install privacy policies. OOBE settings remain answer-file data; `PrivacyConfigurationService` accepts only `PrivacySettings` and a Windows target, never `OobeSettings`.

## Supported targets

The service creates write operations only for Windows 11 Pro or Enterprise, amd64, build 26100 or later. A plan containing a policy intent for another target has an error and `Apply` performs no writes.

## Implemented policy mappings

| Profile preference | Registry policy | Value | Result |
| --- | --- | ---: | --- |
| Location services: enabled | `AppPrivacy\LetAppsAccessLocation` | `1` | Force allow |
| Location services: disabled | `AppPrivacy\LetAppsAccessLocation` | `2` | Force deny |
| Advertising ID: disabled | `AdvertisingInfo\DisabledByGroupPolicy` | `1` | Turn off advertising ID |
| Online speech recognition: disabled | `InputPersonalization\AllowInputPersonalization` | `0` | Prevent online speech services |

The full keys are under `HKLM\Software\Policies\Microsoft`. Each policy is reread after writing and the result reports the actual DWORD value.

`notConfigured` and `userChoice` are always no-ops. They do not delete or overwrite existing organization policy. The service warns instead of inventing a mapping for unsupported privacy fields or for requests to force-enable advertising ID or online speech recognition.

The Windows registry store is an explicit adapter, not part of profile loading, preview, dry run, or package export. No current desktop screen calls it. Test it in Windows SIM and a disposable VM before using it on a workstation.

Policy sources: [Privacy Policy CSP](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-privacy).
