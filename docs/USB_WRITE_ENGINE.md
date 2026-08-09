# USB write engine

Easyaller now has a target-agnostic USB media write engine. It prepares Windows Setup files and an already exported Easyaller deployment package for a future removable-media target. It does not choose a disk, format media, or expose a Windows drive-letter target yet.

## Plan before execution

`UsbMediaWriteEngine.CreatePlan` accepts only an explicitly selected disk that has already passed the removable-disk safety checks. It reads the source setup-media directory and the deployment package directory, records every output path, byte length, and SHA-256 hash, and blocks the plan when:

- either source directory is missing, relative, or unreadable;
- a source contains a symbolic link or another reparse point;
- the deployment manifest is invalid, incomplete, or has a changed file;
- `autounattend.xml` is absent from the verified package;
- setup media and deployment package would write to the same destination path.

The package manifest itself is included in the plan even though it deliberately does not hash itself.

## Execution contract

Execution requires the consumed one-time authorization returned by `AuthorizeFirstWrite`. Its immutable disk ID and optional serial must match the planned selection. Before opening the target, the engine rechecks every planned source hash. A changed source stops the run before copying starts.

The target is an explicit `IUsbMediaWriteTarget` adapter. It receives only the rechecked authorized disk and the immutable plan. The engine copies each source, commits the target only after all writes complete, then rereads every final file through the adapter and compares its length and SHA-256 with the plan. It reports `IsReady` only after every final hash matches. Any read, write, commit, or verification failure returns a non-ready result; a partially copied target is never reported as ready.

## Current boundary

There is intentionally no built-in Windows volume adapter yet. A future adapter must prove that the selected USB volume belongs to the rechecked immutable disk identity before it can implement `IUsbMediaWriteTarget`. This prevents the engine from accepting an arbitrary path that could point at a system disk. The desktop UI and physical USB writes remain future work.
