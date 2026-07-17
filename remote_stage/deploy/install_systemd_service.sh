#!/bin/sh
set -eu

SERVICE_NAME=spacetest3576.service
SERVICE_SRC="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)/$SERVICE_NAME"
SERVICE_DST="/etc/systemd/system/$SERVICE_NAME"
APP_DIR="/home/originflow/work/spaceTest3576"
APP_BIN="$APP_DIR/spacetest3576"

if [ ! -f "$SERVICE_SRC" ]; then
    echo "service template not found: $SERVICE_SRC" >&2
    exit 1
fi

if [ ! -x "$APP_BIN" ]; then
    echo "binary not found or not executable: $APP_BIN" >&2
    exit 1
fi

cp "$SERVICE_SRC" "$SERVICE_DST"
chmod 0644 "$SERVICE_DST"
systemctl daemon-reload
systemctl enable "$SERVICE_NAME"
systemctl restart "$SERVICE_NAME"
systemctl --no-pager --full status "$SERVICE_NAME" || true
