# TF Card Hardware Check

This module checks the TF card from C code.

Current behavior:

- Detects whether the configured block device exists.
- Reads filesystem type with `blkid`.
- Formats to ext4 only when `allow_format_ext4` is enabled.
- Creates or reuses the mount point.
- Mounts the TF card automatically.
- Reads capacity with `statvfs`.
- Performs a small write/read verification file.

Data intended for the upper PC:

- device path
- filesystem
- mount point
- whether formatting happened
- total/free capacity
- read/write result
- final error code and message

Manual test example:

```sh
TF_DEVICE=/dev/mmcblk1p1 TF_MOUNT_POINT=/mnt/spacetest_tf TF_ALLOW_FORMAT_EXT4=1 ./tf_card_test
```

Formatting is destructive. Keep `TF_ALLOW_FORMAT_EXT4=0` unless production test explicitly allows ext4 formatting.
