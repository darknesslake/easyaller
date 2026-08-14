# Proxy and bypass-list profiles

Easyaller profiles can optionally store a proxy address and bypass list together with the existing `promptAtRuntime` proxy mode. Both are profile data, so they are shown as confidential during export review.

Русская версия: [PROXY_BYPASS_RU.md](PROXY_BYPASS_RU.md).

## Profile contract

`machine.proxy.bypassList` is optional and is accepted only when `machine.proxy.mode` is `promptAtRuntime`.

- Enter up to 256 entries in the Profile editor, separated with commas, semicolons, or new lines.
- Each entry may be a hostname, IPv4 address, wildcard pattern such as `*.example.test`, or the documented WinHTTP `<local>` token.
- Entries cannot contain whitespace, URLs, command text, or duplicate values.
- The proxy address is profile data, like the bypass list; it is confidential but not secret and is shown for review before export. The app never stores a proxy password or token.

Example neutral profile fragment:

```json
"proxy": {
  "mode": "promptAtRuntime",
  "bypassList": ["*.example.test", "<local>", "192.0.2.53"]
}
```

## What Apply changes

After an operator validates the proxy address and explicitly confirms the action, the Windows adapter writes the same address and bypass list to two independent Windows proxy stores in one operation:

1. **WinHTTP** — the fixed PowerShell `Set-WinhttpProxy` command. This is the machine-wide store consumed by Windows Update and other background services. It is not visible in Settings.
2. **WinINET** — the current user's `HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings` values (`ProxyEnable`, `ProxyServer`, `ProxyOverride`). This is the same store edited by **Settings → Network & Internet → Proxy** and consumed by the browser and most desktop applications. Easyaller notifies running processes of the change through the documented `InternetSetOption` WinINET API (`INTERNET_OPTION_SETTINGS_CHANGED` and `INTERNET_OPTION_REFRESH`) so it takes effect without a sign-out.

These two stores are otherwise independent and do not synchronize with each other; that is why both are set from the one address in the profile. If either write fails, the WinINET registry values are rolled back to what they were before the attempt.

This does not claim to configure a PAC script, VPN client, or an application with its own separate proxy setting. Review the actual target software before relying on it.

Microsoft documents `Set-WinhttpProxy` in the [WinHttpProxy PowerShell module](https://learn.microsoft.com/en-us/powershell/module/winhttpproxy/set-winhttpproxy?view=windowsserver2025-ps) and `InternetSetOption` in the [WinINET API reference](https://learn.microsoft.com/en-us/windows/win32/api/wininet/nf-wininet-internetsetoptionw). Both are Windows-only and still require Windows VM validation before use on a production workstation.

## Safety boundary

- Easyaller never discovers proxy hosts or bypass values automatically; an operator enters or explicitly imports them from the current PC.
- The proxy address and bypass list are excluded from pending-resume state even though they are stored in the profile.
- The profile field is not an arbitrary PowerShell or WinINET argument and cannot carry a command.
- Export review identifies the bypass list as confidential because it can reveal internal network structure.
- No Windows VM or physical-PC evidence exists for this feature yet.
