# Read-only ISO inspection

Easyaller can inspect a locally supplied Windows ISO before any future USB workflow. The feature is not connected to the desktop interface yet, and it does not select, format, partition, initialize, write to, or otherwise change a disk.

## Checks

The inspection request requires an absolute local `.iso` path, a positive size limit, and the selected deployment target. The default limit is 12 GiB. Before Windows mounts anything, Easyaller verifies that the file exists, is within the limit, and calculates its SHA-256 hash.

On Windows only, a fixed non-interactive PowerShell probe mounts the ISO with `Mount-DiskImage -Access ReadOnly`, finds its volume, and reads only these installation-media facts:

- `setup.exe`, `sources/setup.exe`, and `sources/boot.wim` exist;
- exactly one install image exists: `sources/install.wim` or `sources/install.esd`;
- the image list, edition IDs, architecture, and version are read through `Get-WindowsImage`;
- every exposed image is amd64 and the requested Professional or Enterprise edition is present.

The probe uses `finally` to call `Dismount-DiskImage` by ISO path even when inspection fails. If the probe cannot unmount the image, the command fails and no usable inspection result is returned. The ISO path is passed through the spawned process environment, not interpolated into the PowerShell command.

`Mount-DiskImage` supports `ReadOnly` access, `Get-Volume` accepts a disk-image object, and `Dismount-DiskImage` detaches an ISO by full image path. [Mount-DiskImage](https://learn.microsoft.com/en-us/powershell/module/storage/mount-diskimage), [Get-Volume](https://learn.microsoft.com/en-us/powershell/module/storage/get-volume), [Dismount-DiskImage](https://learn.microsoft.com/en-us/powershell/module/storage/dismount-diskimage). `Get-WindowsImage` reads image metadata from the selected install image. [Get-WindowsImage](https://learn.microsoft.com/en-us/powershell/module/dism/get-windowsimage)

## Boundaries

- ISO download and validation against a publisher-provided hash are out of scope. Operators must obtain the ISO through their approved channel and compare the returned SHA-256 value themselves.
- The probe is Windows-only. On other systems it returns a blocked result and does not invoke a shell.
- Passing this check does not authorize a USB write. Confirmation, device recheck, and a separate write engine remain future tasks.
