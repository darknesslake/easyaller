# Windows USB-volume binding

Easyaller has a Windows-only binding inspector and a staging write target for the USB write engine. The adapter accepts a requested volume root only after it proves that the root belongs to the same immutable disk identity that passed the one-time authorization.

## Read-only binding

The fixed non-interactive PowerShell query takes the root through the child-process environment, then reads `Get-Volume -FilePath`, `Get-Partition`, `Get-Disk`, and `Win32_DiskDrive`. It collects the disk number, `UniqueId`, serial number, bus, size, and system, boot, read-only, offline, and removable state. It does not run format, partition, clear, mount, unmount, initialize, or set commands.

The target blocks when any of these checks fail:

- the root does not resolve to exactly one partition and disk;
- its root path differs from the requested root;
- its `UniqueId` or available serial does not match the plan and current one-time authorization;
- the refreshed disk is no longer an eligible USB, SD, or MMC removable disk;
- the root is not empty before staging starts.

`Get-Volume` resolves a path to a volume and `Get-Partition` can resolve a partition by volume, drive letter, or disk number. [Get-Volume](https://learn.microsoft.com/en-us/powershell/module/storage/get-volume), [Get-Partition](https://learn.microsoft.com/en-us/powershell/module/storage/get-partition), [Get-Disk](https://learn.microsoft.com/en-us/powershell/module/storage/get-disk)

## Staged target behavior

Files are written below a unique `.easyaller-staging-*` directory in the validated empty root. Before commit, the target checks that no unexpected root entry appeared. It then moves staged top-level entries into the root. The write engine rereads every final file and reports ready only when every SHA-256 matches the immutable plan.

If any check, copy, commit, or verification fails, Easyaller returns a non-ready result. Staging is removed when it can be removed, and a partially committed volume is never reported as ready. The desktop UI is not connected yet, and the adapter has not been executed against a physical USB in this development environment.
