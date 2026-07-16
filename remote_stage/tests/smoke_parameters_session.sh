#!/bin/bash
set -eu

cd /home/originflow/work/spaceTest3576
pgrep -x spacetest3576 | xargs -r kill
./spacetest3576 >/tmp/spacetest3576.log 2>&1 &
pid=$!
sleep 1
exec 3<>/dev/tcp/127.0.0.1/19001
printf '%s\n' '{"protocolVersion":"1.0","sessionId":"smoke-parameters","sn":"SNTEST","commandGroup":"session","command":"start","parameters":{"tests":[{"id":"wifi","parameters":{"ssid":"originflow","routerIp":"192.168.110.1","pingCount":2,"timeoutMs":5000}},{"id":"bluetooth","parameters":{"targetName":"Mate40","minRssi":-70,"scanWindowMs":1000}},{"id":"typec_camera","parameters":{"devicePath":"/dev/video0","streamFrameCount":1,"timeoutMs":3000,"minInterruptCount":30}}]}}' >&3
head -n 7 <&3
exec 3>&-
kill "$pid" 2>/dev/null || true
wait "$pid" 2>/dev/null || true
