# PCBA Application Upgrade Design

## Scope

This document defines the host-to-PCBA application upgrade flow. The current debug device is:

- IP address: `192.168.110.17`
- Service: `pcba-test.service`
- Application: `/vendor/originflow/bin/spacetest3576`

The existing device fleet has no reliable application version management. MD5 is therefore the only upgrade decision criterion.

## Upgrade Decision

1. The host calculates the MD5 of its local `spacetest3576` package.
2. The host queries the device for the MD5 of `/vendor/originflow/bin/spacetest3576`.
3. If the MD5 values are equal, the host skips the upgrade and continues normally.
4. If the MD5 values differ, the host displays an upgrade prompt.
5. If the operator does not choose an option within 5 seconds, the host starts the upgrade automatically.

The prompt should show both MD5 values and provide:

- `Upgrade now`: start immediately.
- `Skip`: skip this upgrade and continue testing.

The host must not start normal tests while the prompt or upgrade workflow is active.

## User Prompt

Suggested message:

```text
The PCBA application does not match the host version.

Device MD5: <device-md5>
Host MD5:   <host-md5>

Upgrade now? The upgrade will start automatically in 5 seconds.
```

The countdown should be visible to the operator:

```text
Application mismatch detected. Automatic upgrade in 5 seconds.
```

If the MD5 query fails, the host must show a communication error and must not start an automatic upgrade.

## MD5 Query Compatibility

The host must not depend on a PCBA protocol command for the upgrade decision. Older devices do not implement `get_md5`. The host reads the file directly through ADB:

```bash
adb shell md5sum /vendor/originflow/bin/spacetest3576
```

The host calculates its own MD5 and compares it with the ADB result. This works with both old and new devices. The device service does not need to be running for this file read.

Newer PCBA software may keep the following optional system command for diagnostics, but the host upgrade flow must not require it:

```json
{
  "protocolVersion": "1.0",
  "commandGroup": "system",
  "command": "get_md5"
}
```

Suggested response:

```json
{
  "status": "passed",
  "data": {
    "appName": "spacetest3576",
    "path": "/vendor/originflow/bin/spacetest3576",
    "md5": "<device-md5>",
    "service": "pcba-test.service",
    "serviceActive": true
  }
}
```

The upgrade workflow is separate from the normal test session and should report explicit phases:

```text
checking_md5
waiting_user_decision
uploading
verifying_upload
stopping_service
backing_up
replacing_binary
starting_service
reconnecting
verifying_service_md5
completed
rollback
failed
skipped
```

## Upgrade Procedure

The host uploads the new binary to a temporary path. It must never overwrite the running binary directly.

```text
1. Calculate the host binary MD5.
2. Run `adb shell md5sum /vendor/originflow/bin/spacetest3576` and read the device MD5.
3. Ask the operator when the MD5 values differ.
4. Upload the binary to:
   /vendor/originflow/bin/spacetest3576.new
5. Verify the temporary file MD5 on the device.
6. Stop pcba-test.service.
7. Back up the current binary as spacetest3576.bak.
8. Set the temporary file mode to 755.
9. Atomically move spacetest3576.new to spacetest3576.
10. Start pcba-test.service.
11. Wait for the service to become active.
12. Reconnect to the service.
13. Run ADB `md5sum` on the replaced application again.
14. Report success only if the final MD5 matches the host MD5.
```

Equivalent service operations are:

```bash
systemctl stop pcba-test.service
cp -p /vendor/originflow/bin/spacetest3576 /vendor/originflow/bin/spacetest3576.bak
chmod 755 /vendor/originflow/bin/spacetest3576.new
mv -f /vendor/originflow/bin/spacetest3576.new /vendor/originflow/bin/spacetest3576
systemctl start pcba-test.service
systemctl is-active --quiet pcba-test.service
```

The upgrade account must have permission to stop and start the service and write the application directory. The host must not accept arbitrary device paths from the operator or from an untrusted response.

## Rollback

If upload verification, service restart, reconnection, or final MD5 verification fails:

```bash
systemctl stop pcba-test.service
mv -f /vendor/originflow/bin/spacetest3576.bak /vendor/originflow/bin/spacetest3576
chmod 755 /vendor/originflow/bin/spacetest3576
systemctl start pcba-test.service
```

The backup must be retained until the new service is active and the final MD5 check succeeds. After success, it may be removed according to the device cleanup policy.

## Failure Rules

- Same MD5: skip the prompt and upgrade.
- Different MD5: show the prompt and start the 5-second countdown.
- `Upgrade now`: start immediately.
- `Skip`: record the mismatch and continue testing.
- No choice after 5 seconds: upgrade automatically.
- MD5 query failure: stop the upgrade decision and show a communication error.
- Temporary-file MD5 mismatch: abort and keep the current service running.
- Service restart failure: roll back and mark the upgrade failed.
- Final MD5 mismatch: roll back and mark the upgrade failed.

## Logging

Every upgrade attempt should record:

- Device IP: `192.168.110.17`
- Device serial number, if available
- Host MD5
- Original device MD5
- Final device MD5
- Operator choice or automatic timeout
- Upgrade phase and error message
- Service state before and after upgrade
- Upgrade result and rollback result

Do not write device passwords or other credentials to this document or to normal application logs.
