# Create installation USB

Easyaller now has a desktop screen for copying an installation USB layout to an already empty, preformatted removable volume from a Windows Setup directory and an exported Easyaller deployment package. It does not format, partition, initialize, or otherwise prepare the volume.

## Safe workflow

1. Open **Create installation USB** from the main sidebar.
2. Refresh the disk list. The screen shows only explicitly eligible removable USB, SD, or MMC disks. It never displays system, boot, fixed, offline, read-only, unknown-bus, or identity-less disks as targets.
3. Select one visible disk manually. Nothing is preselected.
4. Select the root directory of mounted or extracted Windows Setup media and the previously exported Easyaller deployment package. Neither source is modified. Obtain the ISO through an approved channel and perform its separate read-only inspection before use.
5. Create the plan. Easyaller hashes the sources and checks the deployment manifest before it creates a five-minute confirmation.
6. Review the vendor, serial or immutable device ID, disk number, and exact byte size. Type the exact uppercase phrase `ERASE`.
7. Immediately before copying, Easyaller refreshes disk inventory, resolves exactly one drive-letter partition on the authorized disk, verifies that the volume root is empty, and binds the root back to the same disk identity. It stages files, commits them, and verifies every final SHA-256 hash.

## Stop conditions

The flow stops without reporting ready when the device changes, no longer passes safety checks, has zero or multiple drive-letter partitions, its root is not empty, a source changes, paths collide, a copy fails, or an output hash differs.

Use a dedicated recoverable test USB for the first physical run. This pre-alpha feature has unit and temporary-directory coverage but has not yet been validated against a physical USB in this development environment. Keep completed evidence, exported packages, and ISO files outside Git.
