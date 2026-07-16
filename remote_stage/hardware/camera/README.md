# Camera Hardware Check

Current rule:

- PASS when the configured V4L2 device can start streaming and dequeue at
  least one frame.

Future rule:

- After stream is available, also read an exposure interrupt counter.
- PASS only when the exposure counter increases by at least 30 frames.

The exposure counter is intentionally configurable because the current board
does not expose a confirmed file path yet.

Example:

```sh
CAMERA_DEVICE=/dev/video0 ./camera_test
CAMERA_DEVICE=/dev/video0 CAMERA_EXPOSURE_COUNTER=/path/to/exposure_count ./camera_test
```
