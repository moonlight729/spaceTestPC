#!/bin/bash
set -eu

cd /home/originflow/work/spaceTest3576
pgrep -x spacetest3576 | xargs -r kill
./spacetest3576 >/tmp/spacetest3576.log 2>&1 &
pid=$!
sleep 1
exec 3<>/dev/tcp/127.0.0.1/19001
printf '%s\n' '{"protocolVersion":"1.0","sessionId":"smoke-order-unsupported","sn":"SNTEST","commandGroup":"session","command":"start","parameters":{"tests":[{"id":"board_state","parameters":{}},{"id":"hdmi","parameters":{}},{"id":"wifi","parameters":{}},{"id":"lcd","parameters":{}},{"id":"typec_camera","parameters":{}}]}}' >&3
head -n 11 <&3
exec 3>&-
kill "$pid" 2>/dev/null || true
wait "$pid" 2>/dev/null || true
