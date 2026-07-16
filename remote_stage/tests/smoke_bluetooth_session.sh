#!/bin/bash
set -eu

cd /home/originflow/work/spaceTest3576
pgrep -x spacetest3576 | xargs -r kill
./spacetest3576 >/tmp/spacetest3576.log 2>&1 &
pid=$!
sleep 1
exec 3<>/dev/tcp/127.0.0.1/19001
printf '%s\n' '{"protocolVersion":"1.0","sessionId":"smoke-bluetooth","sn":"SNTEST","commandGroup":"session","command":"start","parameters":{"tests":[{"id":"bluetooth","parameters":{}}]}}' >&3
head -n 3 <&3
exec 3>&-
kill "$pid" 2>/dev/null || true
wait "$pid" 2>/dev/null || true
