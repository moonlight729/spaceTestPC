#!/usr/bin/env bash
set -u

IFACE="${IFACE:-end0}"
ROUTER_IP="${ROUTER_IP:-192.168.31.1}"
CYCLES="${CYCLES:-5}"
PING_COUNT="${PING_COUNT:-2}"
PING_RETRIES="${PING_RETRIES:-3}"
IP_RETRIES="${IP_RETRIES:-3}"
WAIT_IP_SECONDS="${WAIT_IP_SECONDS:-15}"
WAIT_LINK_SECONDS="${WAIT_LINK_SECONDS:-10}"
SETTLE_SECONDS="${SETTLE_SECONDS:-2}"
CONTINUE_ON_FAIL="${CONTINUE_ON_FAIL:-1}"
NM_DISCONNECT="${NM_DISCONNECT:-0}"
LOG_FILE="${LOG_FILE:-/tmp/spacetest_ethernet_speed_stress.log}"

PASS_COUNT=0
FAIL_COUNT=0

log() {
    local line
    line="$(date '+%F %T') $*"
    printf '%s\n' "$line"
    printf '%s\n' "$line" >> "$LOG_FILE"
}

run_cmd() {
    if [ "$(id -u)" -eq 0 ]; then
        "$@"
    else
        sudo "$@"
    fi
}

read_speed() {
    cat "/sys/class/net/$IFACE/speed" 2>/dev/null || true
}

read_carrier() {
    cat "/sys/class/net/$IFACE/carrier" 2>/dev/null || true
}

read_ipv4() {
    ip -4 -o addr show dev "$IFACE" 2>/dev/null | awk '{print $4}' | cut -d/ -f1 | head -n1
}

reconnect_interface() {
    if [ "$NM_DISCONNECT" = "1" ]; then
        nmcli device disconnect "$IFACE" >/dev/null 2>&1 || true
    fi
    run_cmd ip link set dev "$IFACE" up >/dev/null 2>&1 || true
    nmcli device reapply "$IFACE" >/dev/null 2>&1 || true
    nmcli device connect "$IFACE" >/dev/null 2>&1 || true
}

wait_link_up() {
    local elapsed=0
    while [ "$elapsed" -lt "$WAIT_LINK_SECONDS" ]; do
        if [ "$(read_carrier)" = "1" ]; then
            return 0
        fi
        sleep 1
        elapsed=$((elapsed + 1))
    done
    return 1
}

wait_ipv4() {
    local elapsed=0
    local ip_addr
    while [ "$elapsed" -lt "$WAIT_IP_SECONDS" ]; do
        ip_addr="$(read_ipv4)"
        if [ -n "$ip_addr" ]; then
            printf '%s\n' "$ip_addr"
            return 0
        fi
        sleep 1
        elapsed=$((elapsed + 1))
    done
    return 1
}

restore_network() {
    log "restore: set $IFACE autoneg on and reconnect"
    run_cmd ethtool -s "$IFACE" autoneg on advertise 0x0028 >/dev/null 2>&1 || true
    NM_DISCONNECT=0 reconnect_interface
}

dump_network_state() {
    log "state iface=$IFACE carrier=$(read_carrier) speed=$(read_speed) ip=$(read_ipv4)"
    ip -4 -o addr show dev "$IFACE" 2>/dev/null | while read -r line; do log "state ip_addr $line"; done
    ip route 2>/dev/null | grep "$IFACE" | while read -r line; do log "state route $line"; done
    nmcli -t -f DEVICE,STATE,CONNECTION device status 2>/dev/null | grep "^$IFACE:" | while read -r line; do log "state nmcli $line"; done
}

ping_router_with_retry() {
    local attempt=1
    while [ "$attempt" -le "$PING_RETRIES" ]; do
        if ping -I "$IFACE" -c "$PING_COUNT" -W 2 "$ROUTER_IP" >/dev/null 2>&1; then
            printf '%s\n' "$attempt"
            return 0
        fi
        log "ping_retry attempt=$attempt retries=$PING_RETRIES iface=$IFACE router=$ROUTER_IP"
        sleep 1
        attempt=$((attempt + 1))
    done
    return 1
}

test_phase() {
    local cycle="$1"
    local label="$2"
    local expected_speed="$3"
    local ip_addr
    local actual_speed
    local ping_attempt
    local ip_attempt=1

    log "cycle=$cycle phase=$label switch_start expected=${expected_speed}Mb/s"
    if [ "$expected_speed" = "100" ]; then
        if ! run_cmd ethtool -s "$IFACE" speed 100 duplex full autoneg off; then
            log "cycle=$cycle phase=$label result=FAIL reason=ethtool_100m_failed"
            FAIL_COUNT=$((FAIL_COUNT + 1))
            return 1
        fi
    else
        if ! run_cmd ethtool -s "$IFACE" autoneg on advertise 0x0028; then
            log "cycle=$cycle phase=$label result=FAIL reason=ethtool_1000m_failed"
            FAIL_COUNT=$((FAIL_COUNT + 1))
            return 1
        fi
    fi

    sleep "$SETTLE_SECONDS"

    if ! wait_link_up; then
        log "cycle=$cycle phase=$label result=FAIL reason=link_down"
        FAIL_COUNT=$((FAIL_COUNT + 1))
        return 1
    fi

    actual_speed="$(read_speed)"
    if [ "$actual_speed" != "$expected_speed" ]; then
        log "cycle=$cycle phase=$label result=FAIL reason=speed_mismatch expected=$expected_speed actual=${actual_speed:-unknown}"
        FAIL_COUNT=$((FAIL_COUNT + 1))
        return 1
    fi

    while [ "$ip_attempt" -le "$IP_RETRIES" ]; do
        reconnect_interface
        if ip_addr="$(wait_ipv4)"; then
            break
        fi
        log "ip_retry attempt=$ip_attempt retries=$IP_RETRIES iface=$IFACE speed=$actual_speed"
        dump_network_state
        sleep 1
        ip_attempt=$((ip_attempt + 1))
    done
    if [ "$ip_attempt" -gt "$IP_RETRIES" ]; then
        log "cycle=$cycle phase=$label result=FAIL reason=no_ip speed=$actual_speed"
        FAIL_COUNT=$((FAIL_COUNT + 1))
        return 1
    fi

    if ! ping_attempt="$(ping_router_with_retry)"; then
        log "cycle=$cycle phase=$label result=FAIL reason=router_ping_failed ip=$ip_addr speed=$actual_speed router=$ROUTER_IP"
        FAIL_COUNT=$((FAIL_COUNT + 1))
        return 1
    fi

    PASS_COUNT=$((PASS_COUNT + 1))
    log "cycle=$cycle phase=$label result=PASS ip=$ip_addr speed=${actual_speed}Mb/s router=$ROUTER_IP pingCount=$PING_COUNT ipAttempt=$ip_attempt pingAttempt=$ping_attempt"
    return 0
}

main() {
    : > "$LOG_FILE"
    trap restore_network EXIT INT TERM
    log "start iface=$IFACE router=$ROUTER_IP cycles=$CYCLES pingCount=$PING_COUNT pingRetries=$PING_RETRIES ipRetries=$IP_RETRIES settleSeconds=$SETTLE_SECONDS continueOnFail=$CONTINUE_ON_FAIL nmDisconnect=$NM_DISCONNECT"

    if ! command -v ethtool >/dev/null 2>&1; then
        log "result=FAIL reason=missing_ethtool"
        exit 2
    fi
    if ! command -v nmcli >/dev/null 2>&1; then
        log "result=FAIL reason=missing_nmcli"
        exit 2
    fi
    if [ ! -e "/sys/class/net/$IFACE" ]; then
        log "result=FAIL reason=missing_interface iface=$IFACE"
        exit 2
    fi

    for cycle in $(seq 1 "$CYCLES"); do
        if ! test_phase "$cycle" "100m" "100" && [ "$CONTINUE_ON_FAIL" != "1" ]; then
            break
        fi
        if ! test_phase "$cycle" "1000m" "1000" && [ "$CONTINUE_ON_FAIL" != "1" ]; then
            break
        fi
    done

    restore_network
    log "summary pass=$PASS_COUNT fail=$FAIL_COUNT total=$((CYCLES * 2)) log=$LOG_FILE"

    if [ "$FAIL_COUNT" -eq 0 ] && [ "$PASS_COUNT" -eq $((CYCLES * 2)) ]; then
        log "result=PASS"
        exit 0
    fi

    log "result=FAIL"
    exit 1
}

main "$@"
