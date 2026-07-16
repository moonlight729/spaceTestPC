#!/bin/bash
set -eu

cd /home/originflow/work/spaceTest3576
./spacetest3576 >/tmp/spacetest3576.log 2>&1 &
pid=$!
sleep 1
exec 3<>/dev/tcp/127.0.0.1/19001
printf '%s\n' '{"protocolVersion":"1.0","sessionId":"smoke-session","sn":"SNTEST","commandGroup":"session","command":"start","parameters":{"tests":[{"id":"board_state","parameters":{}},{"id":"fingerprint","parameters":{}}]}}' >&3
head -n 5 <&3
exec 3>&-
kill "$pid" 2>/dev/null || true
wait "$pid" 2>/dev/null || true
