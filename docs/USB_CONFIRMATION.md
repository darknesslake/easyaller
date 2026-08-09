# Destructive USB confirmation

Easyaller now has a state machine that protects any future USB-write engine. It still does not format, initialize, partition, clear, mount, write to, or otherwise change a disk.

## Confirmation flow

The flow starts only with an explicitly selected disk that passes the removable-disk safety model. It creates a five-minute confirmation prompt containing the vendor, serial number or immutable device ID, disk number, and exact byte size. There is no default disk and no automatic confirmation.

The operator must type the exact uppercase phrase `ERASE`. Case changes, leading or trailing whitespace, and every other phrase remain blocked. A successful phrase does not authorize a future write by itself. Immediately before the first write, a future engine must call `AuthorizeFirstWrite`, which refreshes the inventory through the existing immutable-ID and serial-number checks.

If the selected disk disappeared, was duplicated, changed serial number, became unsafe, or was replaced at the same disk number, authorization is blocked. A confirmation expires after five minutes. Successful authorization is consumed once and cannot be replayed.

## Boundaries

- The confirmation is an in-memory, process-local object. Restarting the app cancels it.
- It is not connected to the desktop UI yet.
- No write engine exists yet. WP-053 will receive a one-time authorization only after the final inventory recheck.
