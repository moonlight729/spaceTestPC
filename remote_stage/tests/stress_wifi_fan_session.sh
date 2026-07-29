#!/bin/bash
set -u

HOST="${HOST:-127.0.0.1}"
PORT="${PORT:-19001}"
COUNT="${COUNT:-100}"
SN="${SN:-STRESS-WIFI-FAN}"
LOG="${LOG:-/tmp/spacetest_wifi_fan_stress.jsonl}"
SUMMARY="${SUMMARY:-/tmp/spacetest_wifi_fan_stress_summary.txt}"
START_LOCAL="${START_LOCAL:-0}"
APP_DIR="${APP_DIR:-/home/originflow/work/spaceTest3576}"
APP_BIN="${APP_BIN:-./spacetest3576}"
APP_LOG="${APP_LOG:-/tmp/spacetest3576_stress_$$.log}"
KILL_EXISTING="${KILL_EXISTING:-0}"

SSID="${SSID:-originflow}"
WIFI_IF="${WIFI_IF:-}"
MIN_RSSI="${MIN_RSSI:--75}"
MAX_RETRY_COUNT="${MAX_RETRY_COUNT:-1}"
RETRY_INTERVAL_MS="${RETRY_INTERVAL_MS:-500}"
DECISION_TIMEOUT_MS="${DECISION_TIMEOUT_MS:-5000}"
SCAN_TIMEOUT_MS="${SCAN_TIMEOUT_MS:-10000}"

PWM_PATH="${PWM_PATH:-/sys/class/hwmon/hwmon12/pwm1}"
TACH_PATH="${TACH_PATH:-/sys/class/hwmon/hwmon12/tach_rpm}"
FAN_START_VALUE="${FAN_START_VALUE:-100}"
FAN_STOP_VALUE="${FAN_STOP_VALUE:-0}"
FAN_SETTLE_MS="${FAN_SETTLE_MS:-1000}"
FAN_TACH_SAMPLE_COUNT="${FAN_TACH_SAMPLE_COUNT:-3}"
FAN_TACH_SAMPLE_INTERVAL_MS="${FAN_TACH_SAMPLE_INTERVAL_MS:-300}"

pid=""

cleanup() {
    if [ -n "$pid" ]; then
        kill "$pid" 2>/dev/null || true
        wait "$pid" 2>/dev/null || true
    fi
}
trap cleanup EXIT

json_escape() {
    printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g'
}

json_get_string() {
    local key="$1"
    sed -n "s/.*\"$key\":\"\\([^\"]*\\)\".*/\\1/p"
}

json_get_int() {
    local key="$1"
    sed -n "s/.*\"$key\":\\(-\\{0,1\\}[0-9][0-9]*\\).*/\\1/p"
}

json_get_bool() {
    local key="$1"
    sed -n "s/.*\"$key\":\\(true\\|false\\).*/\\1/p"
}

summary_value() {
    local value="$1"
    if [ -z "$value" ]; then
        printf '-'
    else
        printf '%s' "$value" | tr ' \t' '__' | tr -d '\r\n'
    fi
}

send_wifi_decision_if_needed() {
    local session_id="$1"
    local line="$2"
    local found rssi min_rssi passed reason

    printf '%s' "$line" | grep -q '"testId":"wifi"' || return 0
    printf '%s' "$line" | grep -q '"status":"running"' || return 0
    printf '%s' "$line" | grep -q '"readyForHostDecision":true' || return 0

    found="$(printf '%s' "$line" | json_get_bool found)"
    rssi="$(printf '%s' "$line" | json_get_int rssi)"
    min_rssi="$(printf '%s' "$line" | json_get_int minRssi)"

    if [ "$found" = "true" ] && [ -n "$rssi" ] && [ -n "$min_rssi" ] && [ "$rssi" -ge "$min_rssi" ]; then
        passed="true"
        reason="stress_auto_pass"
    else
        passed="false"
        reason="stress_auto_fail"
    fi

    printf '{"event":"test.decision","source":"stress_script","sessionId":"%s","testId":"wifi","passed":%s,"reason":"%s","timestamp":"%s"}\n' \
        "$session_id" "$passed" "$reason" "$(date -Iseconds)" >&3
}

run_one_iteration() {
    local index="$1"
    local session_id="stress-wifi-fan-$(date +%Y%m%d%H%M%S)-$index"
    local escaped_ssid escaped_wifi_if escaped_pwm escaped_tach
    local line test_id status code fan_status="" wifi_status="" final_status="" final_code=""
    local wifi_message="" wifi_reason="" wifi_found="" wifi_rssi="" wifi_min_rssi="" wifi_attempt=""
    local wifi_scan_retry_count=""
    local fan_message="" fan_tach="" fan_tach_read="" fan_pwm_stopped="" fan_samples_read=""
    local final_message=""

    escaped_ssid="$(json_escape "$SSID")"
    escaped_wifi_if="$(json_escape "$WIFI_IF")"
    escaped_pwm="$(json_escape "$PWM_PATH")"
    escaped_tach="$(json_escape "$TACH_PATH")"

    exec 3<>"/dev/tcp/$HOST/$PORT" || return 2
    printf '{"protocolVersion":"1.0","sessionId":"%s","sn":"%s","commandGroup":"session","command":"start","parameters":{"tests":[{"id":"wifi","parameters":{"ssid":"%s","interfaceName":"%s","minRssi":%s,"maxRetryCount":%s,"retryIntervalMs":%s,"decisionTimeoutMs":%s,"scanTimeoutMs":%s}},{"id":"fan","parameters":{"mode":"finished_product","pwmPath":"%s","tachPath":"%s","startValue":%s,"stopValue":%s,"tachSettleMs":%s,"tachSampleCount":%s,"tachSampleIntervalMs":%s}}]}}\n' \
        "$session_id" "$SN" "$escaped_ssid" "$escaped_wifi_if" "$MIN_RSSI" "$MAX_RETRY_COUNT" "$RETRY_INTERVAL_MS" "$DECISION_TIMEOUT_MS" "$SCAN_TIMEOUT_MS" \
        "$escaped_pwm" "$escaped_tach" "$FAN_START_VALUE" "$FAN_STOP_VALUE" "$FAN_SETTLE_MS" "$FAN_TACH_SAMPLE_COUNT" "$FAN_TACH_SAMPLE_INTERVAL_MS" >&3

    while IFS= read -r line <&3; do
        printf '{"iteration":%s,"receivedAt":"%s","payload":%s}\n' "$index" "$(date -Iseconds)" "$line" >>"$LOG"
        send_wifi_decision_if_needed "$session_id" "$line"

        if printf '%s' "$line" | grep -q '"event":"test.report"'; then
            test_id="$(printf '%s' "$line" | json_get_string testId)"
            status="$(printf '%s' "$line" | json_get_string status)"
            code="$(printf '%s' "$line" | json_get_int resultCode)"
            if [ "$test_id" = "wifi" ] && { [ "$status" = "passed" ] || [ "$status" = "failed" ]; }; then
                wifi_status="$status/$code"
                wifi_message="$(printf '%s' "$line" | json_get_string message)"
                wifi_reason="$(printf '%s' "$line" | json_get_string failureReason)"
                wifi_found="$(printf '%s' "$line" | json_get_bool found)"
                wifi_rssi="$(printf '%s' "$line" | json_get_int rssi)"
                wifi_min_rssi="$(printf '%s' "$line" | json_get_int minRssi)"
                wifi_attempt="$(printf '%s' "$line" | json_get_int attempt)"
                wifi_scan_retry_count="$(printf '%s' "$line" | json_get_int scanRetryCount)"
            fi
            if [ "$test_id" = "fan" ] && { [ "$status" = "passed" ] || [ "$status" = "failed" ]; }; then
                fan_status="$status/$code"
                fan_message="$(printf '%s' "$line" | json_get_string message)"
                fan_tach="$(printf '%s' "$line" | json_get_int tachRpm)"
                fan_tach_read="$(printf '%s' "$line" | json_get_bool tachRead)"
                fan_pwm_stopped="$(printf '%s' "$line" | json_get_bool pwmStopped)"
                fan_samples_read="$(printf '%s' "$line" | json_get_int tachSamplesRead)"
            fi
        fi

        if printf '%s' "$line" | grep -q '"event":"session.completed"'; then
            final_status="$(printf '%s' "$line" | json_get_string status)"
            final_code="$(printf '%s' "$line" | json_get_int resultCode)"
            final_message="$(printf '%s' "$line" | json_get_string message)"
            break
        fi
    done

    exec 3>&-
    printf '%s iteration=%s session=%s final=%s/%s finalMessage=%s wifi=%s wifiMessage=%s wifiReason=%s wifiFound=%s wifiRssi=%s wifiMinRssi=%s wifiAttempt=%s wifiScanRetryCount=%s fan=%s fanMessage=%s fanTachRpm=%s fanTachRead=%s fanTachSamplesRead=%s fanPwmStopped=%s\n' \
        "$(date -Iseconds)" "$index" "$session_id" "${final_status:-unknown}" "${final_code:-unknown}" \
        "$(summary_value "$final_message")" \
        "${wifi_status:-missing}" "$(summary_value "$wifi_message")" "$(summary_value "$wifi_reason")" \
        "$(summary_value "$wifi_found")" "$(summary_value "$wifi_rssi")" "$(summary_value "$wifi_min_rssi")" "$(summary_value "$wifi_attempt")" "$(summary_value "$wifi_scan_retry_count")" \
        "${fan_status:-missing}" "$(summary_value "$fan_message")" "$(summary_value "$fan_tach")" \
        "$(summary_value "$fan_tach_read")" "$(summary_value "$fan_samples_read")" "$(summary_value "$fan_pwm_stopped")" >>"$SUMMARY"

    [ "$final_status" = "passed" ]
}

if [ "$START_LOCAL" = "1" ]; then
    cd "$APP_DIR"
    if [ "$KILL_EXISTING" = "1" ]; then
        pgrep -x spacetest3576 | xargs -r kill || true
    fi
    "$APP_BIN" >"$APP_LOG" 2>&1 &
    pid="$!"
    sleep 1
fi

: >"$LOG"
: >"$SUMMARY"

pass_count=0
fail_count=0

for i in $(seq 1 "$COUNT"); do
    echo "[$i/$COUNT] running wifi+fan stress session..."
    if run_one_iteration "$i"; then
        pass_count=$((pass_count + 1))
        echo "[$i/$COUNT] PASS"
    else
        fail_count=$((fail_count + 1))
        echo "[$i/$COUNT] FAIL"
    fi
done

echo "pass=$pass_count fail=$fail_count total=$COUNT"
echo "summary: $SUMMARY"
echo "jsonl: $LOG"

[ "$fail_count" -eq 0 ]
