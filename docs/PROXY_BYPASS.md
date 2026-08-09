# WinHTTP proxy bypass profiles

Easyaller profiles can optionally store a WinHTTP proxy bypass list together with the existing `promptAtRuntime` proxy mode. The proxy server address remains a runtime-only value. The bypass list is profile data, so it is shown as confidential during export review.

Русская версия: [PROXY_BYPASS_RU.md](PROXY_BYPASS_RU.md).

## Profile contract

`machine.proxy.bypassList` is optional and is accepted only when `machine.proxy.mode` is `promptAtRuntime`.

- Enter up to 256 entries in the Profile editor, separated with commas, semicolons, or new lines.
- Each entry may be a hostname, IPv4 address, wildcard pattern such as `*.example.test`, or the documented WinHTTP `<local>` token.
- Entries cannot contain whitespace, URLs, command text, or duplicate values.
- The app does not store a proxy password, token, or the runtime proxy address in the reusable profile.

Example neutral profile fragment:

```json
"proxy": {
  "mode": "promptAtRuntime",
  "bypassList": ["*.example.test", "<local>", "192.0.2.53"]
}
```

## What Apply changes

After an operator validates the runtime proxy address and types `APPLY`, the Windows adapter uses the fixed PowerShell `Set-WinhttpProxy` command. When the saved list is non-empty, it passes it through the command's `BypassList` parameter. When it is empty, it sets only the proxy address.

This setting applies to WinHTTP consumers. It does not claim to configure every browser, desktop application, PAC script, VPN client, or user-specific proxy setting. Review the actual target software before relying on it.

Microsoft documents `Set-WinhttpProxy` and its optional bypass-list parameter in the [WinHttpProxy PowerShell module](https://learn.microsoft.com/en-us/powershell/module/winhttpproxy/set-winhttpproxy?view=windowsserver2025-ps). The command is Windows-only and still requires Windows VM validation before use on a production workstation.

## Safety boundary

- Easyaller never discovers proxy hosts or bypass values automatically.
- The operator supplies the proxy address only at runtime; it is excluded from profile storage and pending-resume state.
- The profile field is not an arbitrary PowerShell argument and cannot carry a command.
- Export review identifies the bypass list as confidential because it can reveal internal network structure.
- No Windows VM or physical-PC evidence exists for this feature yet.
