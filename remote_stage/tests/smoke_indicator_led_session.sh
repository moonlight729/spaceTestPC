#!/bin/bash
set -u

HOST="${HOST:-127.0.0.1}"
PORT="${PORT:-19001}"
COUNT="${COUNT:-1}"
SN="${SN:-LED-SMOKE}"
PHASE_DURATION_MS="${PHASE_DURATION_MS:-2000}"
RED_GREEN_OVERLAP_MS="${RED_GREEN_OVERLAP_MS:-200}"
TIMEOUT_MS="${TIMEOUT_MS:-30000}"
I2C_TIMEOUT_MS="${I2C_TIMEOUT_MS:-3000}"
I2C_RETRY_INTERVAL_MS="${I2C_RETRY_INTERVAL_MS:-100}"
AUTO_PASS="${AUTO_PASS:-1}"
LOG="${LOG:-/tmp/spacetest_indicator_led_smoke.jsonl}"
SUMMARY="${SUMMARY:-/tmp/spacetest_indicator_led_smoke_summary.txt}"

json_get_string() {
    local key="$1"
    sed -n "s/.*\"$key\":\"\\([^\"]*\\)\".*/\\1/p"
}

run_one_iteration() {
    local index="$1"
    local session_id="smoke-indicator-led-$(date +%Y%m%d%H%M%S)-$index"
    local decision_sent=0
    local line status final_code

    exec 3<>"/dev/tcp/$HOST/$PORT" || {
        echo "Unable to connect to $HOST:$PORT" >&2
        printf '%s iteration=%s session=%s final=connect_failed\n' "$(date -Iseconds)" "$index" "$session_id" >>"$SUMMARY"
        return 1
    }

    printf '{"protocolVersion":"1.0","sessionId":"%s","sn":"%s","commandGroup":"session","command":"start","parameters":{"tests":[{"id":"indicator_led","parameters":{"mode":"finished_product","phaseDurationMs":%s,"redGreenOverlapMs":%s,"timeoutMs":%s,"i2cTimeoutMs":%s,"i2cRetryIntervalMs":%s}}]}}\n' \
        "$session_id" "$SN" "$PHASE_DURATION_MS" "$RED_GREEN_OVERLAP_MS" "$TIMEOUT_MS" "$I2C_TIMEOUT_MS" "$I2C_RETRY_INTERVAL_MS" >&3

    while IFS= read -r line <&3; do
        printf '[%s/%s] %s\n' "$index" "$COUNT" "$line"
        printf '{"iteration":%s,"receivedAt":"%s","payload":%s}\n' "$index" "$(date -Iseconds)" "$line" >>"$LOG"

        if [ "$AUTO_PASS" = "1" ] && [ "$decision_sent" = "0" ] &&
           printf '%s' "$line" | grep -q '"testId":"indicator_led"' &&
           printf '%s' "$line" | grep -q '"currentLed":"blue"'; then
            sleep "$(awk "BEGIN { printf \"%.3f\", ($PHASE_DURATION_MS + 500) / 1000 }")"
            printf '{"event":"test.decision","sessionId":"%s","testId":"indicator_led","passed":true,"reason":"smoke_auto_pass_after_rgb_sequence"}\n' \
                "$session_id" >&3
            decision_sent=1
        fi

        if printf '%s' "$line" | grep -q '"event":"session.completed"'; then
            status="$(printf '%s' "$line" | json_get_string status)"
            final_code="$(printf '%s' "$line" | sed -n 's/.*"resultCode":\(-\{0,1\}[0-9][0-9]*\).*/\1/p')"
            exec 3>&-
            printf '%s iteration=%s session=%s final=%s/%s\n' "$(date -Iseconds)" "$index" "$session_id" "$status" "${final_code:-unknown}" >>"$SUMMARY"
            [ "$status" = "passed" ]
            return $?
        fi
    done

    exec 3>&-
    printf '%s iteration=%s session=%s final=no_completion\n' "$(date -Iseconds)" "$index" "$session_id" >>"$SUMMARY"
    return 1
}

passed_count=0
failed_count=0
: >"$LOG"
: >"$SUMMARY"

index=1
while [ "$index" -le "$COUNT" ]; do
    if run_one_iteration "$index"; then
        passed_count=$((passed_count + 1))
    else
        failed_count=$((failed_count + 1))
    fi
    index=$((index + 1))
done

printf 'indicator_led smoke complete: total=%s passed=%s failed=%s log=%s summary=%s\n' \
    "$COUNT" "$passed_count" "$failed_count" "$LOG" "$SUMMARY"

[ "$failed_count" -eq 0 ]
