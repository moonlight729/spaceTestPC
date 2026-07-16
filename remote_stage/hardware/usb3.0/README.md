# USB 3.0 Hardware Check

This module is a C framework for the future USB 3.0 test.

Current status:

- No real USB 3.0 board file interface is available yet.
- `usb3_open()` creates an unconfigured device.
- `usb3_configure_paths()` will later receive board file paths for present, speed and optional read/write status.
- `usb3_run_test()` returns error code `4500` until those paths are configured.

Expected future files:

- `present`: integer, non-zero means a USB 3.0 device is detected.
- `speed`: integer Mbps, expected minimum is usually `5000`.
- `rw_check`: optional integer, non-zero means read/write verification passed.

The upper-PC project currently has no USB 3.0 test item, so this module is not part of the active PCBA sequence yet.
