# Removable-disk safety model

Easyaller now has a read-only disk inventory and selection model. It does not format, initialize, partition, mount, unmount, clear, set attributes on, or write to a disk.

The Windows inventory provider reads `Get-Disk` and `Win32_DiskDrive` data through a fixed non-interactive PowerShell query. It collects the disk number, vendor, friendly name, serial number, immutable `UniqueId`, bus type, removable status, size, and system, boot, read-only, and offline flags. These fields correspond to the documented Windows `MSFT_Disk` model. [MSFT_Disk reference](https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/msft-disk), [Get-Disk reference](https://learn.microsoft.com/en-us/powershell/module/storage/get-disk)

## Eligibility

No disk is selected by default. A later UI must require an explicit click on a visible candidate, and the safety service permits confirmation only when all conditions hold:

- the disk reports a non-empty immutable `UniqueId`;
- it reports removable media;
- its bus is USB, SD, or MMC;
- it is neither a system nor boot disk;
- it is online, writable, and has a positive size.

Every other disk is blocked, including internal, virtual, unknown-bus, offline, read-only, system, and boot disks.

## Hot-swap protection

Selection stores the immutable `UniqueId`, optional serial number, and the disk number shown at selection time. Before any future confirmation or write, inventory must be refreshed and the selection rechecked:

- a missing ID, duplicate ID, changed serial number, or a different disk at the same number blocks the flow;
- a disk whose number changes but whose immutable ID and serial still match remains the same selected device;
- rechecking eligibility is mandatory, so a device that becomes boot, system, offline, read-only, or non-removable is blocked.

This task intentionally stops before destructive confirmation, ISO inspection, USB writing, and desktop UI. Those remain separate tasks.
