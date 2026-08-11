# Static IPv4 and DNS configuration

Easyaller can store a static IPv4 address, subnet mask, default gateway, and up
to three DNS server addresses in a profile. These are confidential but
non-secret profile values. They are shown in the export preview and must not be
committed when they describe an organization network.

## Profile contract

Set `machine.network.mode` to `staticIpv4` and provide
`machine.network.staticIpv4` with `address`, `subnetMask`, `defaultGateway`,
and `dnsServers`. Existing profiles with `promptAtRuntime` remain unchanged.

`defaultGateway` and `dnsServers` are optional. An empty DNS list is a valid
value: the address, mask, and gateway are applied while the selected adapter
keeps its current DNS servers. An empty `defaultGateway` describes an isolated
subnet with no route off the segment, and no gateway is written.

Easyaller validates IPv4 syntax, a contiguous usable subnet mask, a usable host
address, a gateway in the same subnet when one is given, and, when DNS servers
are listed, up to three unique usable IPv4 DNS server addresses. A profile never
includes Wi-Fi secrets, VPN settings, or raw commands.

## Applying a profile

The operator must still enter one enabled adapter name or interface GUID at
runtime. Easyaller never selects an adapter. After the exact `APPLY`
confirmation and an administrator check, it disables DHCP only for that
adapter, replaces its non-system IPv4 addresses, sets the configured address
and gateway, then sets the ordered DNS server list. When the DNS list is empty
the DNS command is never issued and the adapter keeps its current servers. It
rereads the address, and the DNS list when one was set, before reporting
success. On a command failure it attempts to restore the previous IPv4 and DNS
configuration on the same adapter.

Changing a network address can disconnect the current management connection.
Use a console or an isolated test VM and confirm the assigned address is free
and belongs to the selected switch port before applying it. Windows VM evidence
is still required before using this feature on a workstation.

The implementation uses the documented Windows PowerShell `Set-NetIPInterface`,
`New-NetIPAddress`, and `Set-DnsClientServerAddress` cmdlets. See [Microsoft's
Set-NetIPInterface documentation](https://learn.microsoft.com/en-us/powershell/module/nettcpip/set-netipinterface?view=windowsserver2025-ps), [New-NetIPAddress documentation](https://learn.microsoft.com/en-us/powershell/module/nettcpip/new-netipaddress?view=windowsserver2025-ps), and [Set-DnsClientServerAddress documentation](https://learn.microsoft.com/en-us/powershell/module/dnsclient/set-dnsclientserveraddress?view=windowsserver2025-ps).
