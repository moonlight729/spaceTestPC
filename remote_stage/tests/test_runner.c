#define _POSIX_C_SOURCE 200809L
#include "test_runner.h"

#include "../hardware/fingerprint/fingerprint.h"
#include "../hardware/indicator_led/indicator_led.h"
#include "../hardware/bluetooth/bluetoothctl_scan.h"
#include "../hardware/camera/camera_stream.h"
#include "../hardware/ethernet/ethernet_nmcli.h"
#include "../hardware/fast_charge/fast_charge.h"
#include "../hardware/keys/key_input.h"
#include "../hardware/tf_card/tf_card.h"
#include "../hardware/usb3.0/usb3_file_check.h"
#include "../hardware/usb/usb_insert_test.h"
#include "../hardware/pcba_points/pcba_points_file.h"
#include "../hardware/wifi/wifi_nmcli.h"
#include "../protocol/protocol.h"
#include "../storage/board_state.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <errno.h>
#include <fcntl.h>
#include <stdint.h>
#include <sys/select.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <time.h>
#include <unistd.h>

#define CHARGE_CONTROL_ENABLE_COMMAND "i2ctransfer -f -y 7 w2@0x6b 0x12 0x00"
#define CHARGE_CONTROL_DISABLE_COMMAND "i2ctransfer -f -y 7 w2@0x6b 0x12 0x80"
#define CHARGE_CURRENT_LIMIT_500MA_COMMAND "i2ctransfer -f -y 7 w3@0x6b 0x03 0x00 0x32"
#define CHARGE_CURRENT_LIMIT_MA 500
#define PMIC_STATUS0_READ_COMMAND "i2ctransfer -f -y 7 w1@0x6b 0x1b r1"
#define PMIC_STATUS1_READ_COMMAND "i2ctransfer -f -y 7 w1@0x6b 0x1c r1"

static int wait_test_decision(int fd, const char *test_id, int timeout_ms, int *passed);
static int wait_operator_decision_or_disconnect(int fd, const char *test_id, int timeout_ms, int *passed, int *disconnected);
static int ethernet_led_resume_pending;
static int ethernet_led_resume_code;
static int ethernet_led_sequence_complete;

static void ethernet_led_log(const char *phase, const char *detail)
{
    struct timespec now;
    clock_gettime(CLOCK_REALTIME, &now);
    fprintf(stderr, "[ETHERNET_LED] ts=%lld.%03ld phase=%s %s\n",
            (long long)now.tv_sec, now.tv_nsec / 1000000L,
            phase != NULL ? phase : "unknown", detail != NULL ? detail : "");
    fflush(stderr);
}

static int read_sysfs_long(const char *path, long long *value)
{
    FILE *file;
    char buffer[64];
    char *end;
    if (path == NULL || value == NULL) return -1;
    file = fopen(path, "r");
    if (file == NULL || fgets(buffer, sizeof(buffer), file) == NULL) {
        if (file != NULL) fclose(file);
        return -1;
    }
    fclose(file);
    *value = strtoll(buffer, &end, 10);
    return end == buffer ? -1 : 0;
}

static int read_sysfs_text(const char *path, char *value, size_t value_size)
{
    FILE *file;
    if (path == NULL || value == NULL || value_size == 0) return -1;
    file = fopen(path, "r");
    if (file == NULL || fgets(value, value_size, file) == NULL) {
        if (file != NULL) fclose(file);
        return -1;
    }
    fclose(file);
    value[strcspn(value, "\r\n")] = '\0';
    return 0;
}

static const char *find_object_end(const char *start)
{
    int depth = 0;
    int in_string = 0;
    int escaped = 0;
    const char *p;
    for (p = start; *p != '\0'; ++p) {
        if (escaped) {
            escaped = 0;
            continue;
        }
        if (*p == '\\' && in_string) {
            escaped = 1;
            continue;
        }
        if (*p == '"') {
            in_string = !in_string;
            continue;
        }
        if (in_string) continue;
        if (*p == '{') ++depth;
        if (*p == '}') {
            --depth;
            if (depth == 0) return p + 1;
        }
    }
    return NULL;
}

static int read_next_test(const char **cursor, char *test_id, size_t test_id_size,
                          const char **object_start, const char **object_end)
{
    const char *id_key;
    const char *start;
    const char *value_start;
    const char *value_end;
    size_t length;

    if (cursor == NULL || *cursor == NULL || test_id == NULL || test_id_size == 0 ||
        object_start == NULL || object_end == NULL) return 0;
    id_key = strstr(*cursor, "\"id\"");
    if (id_key == NULL) return 0;
    start = id_key;
    while (start > *cursor && *start != '{') --start;
    if (*start != '{') return 0;
    value_start = strchr(id_key + 4, ':');
    if (value_start == NULL) return 0;
    ++value_start;
    while (*value_start == ' ' || *value_start == '\t') ++value_start;
    if (*value_start != '"') return 0;
    ++value_start;
    value_end = strchr(value_start, '"');
    if (value_end == NULL) return 0;
    length = (size_t)(value_end - value_start);
    if (length >= test_id_size) length = test_id_size - 1;
    memcpy(test_id, value_start, length);
    test_id[length] = '\0';
    *object_start = start;
    *object_end = find_object_end(start);
    if (*object_end == NULL) return 0;
    *cursor = *object_end;
    return 1;
}

static const char *find_key_in_range(const char *start, const char *end, const char *key)
{
    char pattern[80];
    const char *found;
    snprintf(pattern, sizeof(pattern), "\"%s\"", key);
    found = start;
    while ((found = strstr(found, pattern)) != NULL) {
        if (found >= end) return NULL;
        return found;
    }
    return NULL;
}

static int param_string(const char *start, const char *end, const char *key,
                        char *value, size_t value_size)
{
    const char *found;
    const char *p;
    const char *q;
    size_t length;
    if (value == NULL || value_size == 0) return 0;
    found = find_key_in_range(start, end, key);
    if (found == NULL) return 0;
    p = strchr(found, ':');
    if (p == NULL || p >= end) return 0;
    ++p;
    while (p < end && (*p == ' ' || *p == '\t')) ++p;
    if (p >= end || *p != '"') return 0;
    ++p;
    q = p;
    while (q < end && *q != '"') ++q;
    if (q >= end) return 0;
    length = (size_t)(q - p);
    if (length >= value_size) length = value_size - 1;
    memcpy(value, p, length);
    value[length] = '\0';
    return 1;
}

static int param_int(const char *start, const char *end, const char *key, int fallback)
{
    const char *found;
    const char *p;
    int value;
    found = find_key_in_range(start, end, key);
    if (found == NULL) return fallback;
    p = strchr(found, ':');
    if (p == NULL || p >= end) return fallback;
    ++p;
    while (p < end && (*p == ' ' || *p == '\t')) ++p;
    if (sscanf(p, "%d", &value) != 1) return fallback;
    return value;
}

static int param_bool(const char *start, const char *end, const char *key, int fallback)
{
    const char *found;
    const char *p;
    found = find_key_in_range(start, end, key);
    if (found == NULL) return fallback;
    p = strchr(found, ':');
    if (p == NULL || p >= end) return fallback;
    ++p;
    while (p < end && (*p == ' ' || *p == '\t')) ++p;
    if (p + 4 <= end && strncmp(p, "true", 4) == 0) return 1;
    if (p + 5 <= end && strncmp(p, "false", 5) == 0) return 0;
    return fallback;
}

static int net_carrier_is_up(const char *interface_name)
{
    char path[160];
    FILE *file;
    int value = 0;

    if (interface_name == NULL || interface_name[0] == '\0') return 0;
    snprintf(path, sizeof(path), "/sys/class/net/%s/carrier", interface_name);
    file = fopen(path, "r");
    if (file == NULL) return 0;
    if (fscanf(file, "%d", &value) != 1) {
        fclose(file);
        return 0;
    }
    fclose(file);
    return value == 1;
}

static void sleep_ms_local(int ms)
{
    struct timespec ts;
    ts.tv_sec = ms / 1000;
    ts.tv_nsec = (long)(ms % 1000) * 1000000L;
    nanosleep(&ts, NULL);
}

static double timespec_diff_ms(const struct timespec *start, const struct timespec *end)
{
    double seconds;
    double nanoseconds;
    if (start == NULL || end == NULL) return 0.0;
    seconds = (double)(end->tv_sec - start->tv_sec) * 1000.0;
    nanoseconds = (double)(end->tv_nsec - start->tv_nsec) / 1000000.0;
    return seconds + nanoseconds;
}

static int send_report(int fd, const char *test_id, const char *status,
                       int code, const char *message, const char *data_json)
{
    char line[16384];
    protocol_build_test_report(line, sizeof(line), test_id, status, code, message, data_json);
    return protocol_write_line(fd, line);
}

static int set_charge_enabled(int enabled)
{
    return system(enabled ? CHARGE_CONTROL_ENABLE_COMMAND : CHARGE_CONTROL_DISABLE_COMMAND);
}

static int set_charge_current_limit_500ma(void)
{
    return system(CHARGE_CURRENT_LIMIT_500MA_COMMAND);
}

static int read_i2c_register_value(const char *command, int *value)
{
    FILE *pipe;
    char buffer[64];
    unsigned int parsed;
    if (value == NULL) return -1;
    *value = 0;
    pipe = popen(command, "r");
    if (pipe == NULL) return -1;
    if (fgets(buffer, sizeof(buffer), pipe) == NULL) {
        pclose(pipe);
        return -1;
    }
    pclose(pipe);
    if (sscanf(buffer, "0x%x", &parsed) != 1) return -1;
    *value = (int)(parsed & 0xFFu);
    return 0;
}

static int read_charge_status_bits(int *status0, int *status1,
                                   int *vbus_present, int *pg_stat, int *chg_stat,
                                   int *vbus_stat, int *bc12_done)
{
    int reg1b;
    int reg1c;
    if (read_i2c_register_value(PMIC_STATUS0_READ_COMMAND, &reg1b) != 0 ||
        read_i2c_register_value(PMIC_STATUS1_READ_COMMAND, &reg1c) != 0) {
        return -1;
    }
    if (status0 != NULL) *status0 = reg1b;
    if (status1 != NULL) *status1 = reg1c;
    if (vbus_present != NULL) *vbus_present = reg1b & 0x01;
    if (pg_stat != NULL) *pg_stat = (reg1b >> 3) & 0x01;
    if (chg_stat != NULL) *chg_stat = (reg1c >> 5) & 0x07;
    if (vbus_stat != NULL) *vbus_stat = (reg1c >> 1) & 0x0F;
    if (bc12_done != NULL) *bc12_done = reg1c & 0x01;
    return 0;
}

static const char *map_charge_stage_name(int chg_stat)
{
    switch (chg_stat) {
    case 1: return "trickle";
    case 2: return "precharge";
    case 3: return "cc";
    case 4: return "cv";
    case 6: return "topoff";
    case 7: return "done";
    default: return "not_charging";
    }
}

static const char *map_vbus_type_name(int vbus_stat)
{
    switch (vbus_stat) {
    case 0x0: return "no_input";
    case 0x1: return "usb_sdp";
    case 0x2: return "usb_cdp";
    case 0x3: return "usb_dcp";
    case 0x4: return "hvdcp";
    case 0x5: return "unknown_adapter";
    case 0x6: return "non_standard_adapter";
    case 0x7: return "otg_mode";
    case 0x8: return "not_qualified_adapter";
    case 0xB: return "powered_from_vbus";
    default: return "reserved";
    }
}

static int is_external_charger_type(int vbus_stat)
{
    return vbus_stat == 0x3 || vbus_stat == 0x4 || vbus_stat == 0x5 || vbus_stat == 0x6;
}

static int run_board_state(int fd)
{
    struct board_state state;
    char data[1024];
    send_report(fd, "board_state", "running", 0, "Reading board state", "{}");
    board_state_load_defaults(&state);
    board_state_to_json(&state, data, sizeof(data));
    return send_report(fd, "board_state", "passed", 0, "Board state loaded", data);
}

static int run_fingerprint(int fd)
{
    send_report(fd, "fingerprint", "running", 0, "Running fingerprint test", "{}");
    send_report(fd, "fingerprint", "failed", 4101,
                "Fingerprint module is not implemented on 3576 yet",
                "{\"implemented\":false}");
    return -1;
}

static int run_wifi(int fd, const struct app_config *config, const char *test_start, const char *test_end)
{
    struct wifi_device device;
    char ssid[128];
    char interface_name[32] = "";
    int max_retry_count = 5;
    int retry_interval_ms = 2000;
    int decision_timeout_ms = 5000;
    int scan_timeout_ms = 10000;
    int min_rssi = -55;
    int attempt;
    struct wifi_request request = {
        .ssid = ssid,
        .scan_timeout_ms = scan_timeout_ms,
    };
    struct wifi_result result;
    char data[1024];
    int decision_passed = 0;

    snprintf(ssid, sizeof(ssid), "%s", config->wifi_ssid);
    param_string(test_start, test_end, "ssid", ssid, sizeof(ssid));
    param_string(test_start, test_end, "interfaceName", interface_name, sizeof(interface_name));
    max_retry_count = param_int(test_start, test_end, "maxRetryCount", max_retry_count);
    retry_interval_ms = param_int(test_start, test_end, "retryIntervalMs", retry_interval_ms);
    decision_timeout_ms = param_int(test_start, test_end, "decisionTimeoutMs", decision_timeout_ms);
    scan_timeout_ms = param_int(test_start, test_end, "scanTimeoutMs", scan_timeout_ms);
    min_rssi = param_int(test_start, test_end, "minRssi", min_rssi);
    if (max_retry_count <= 0) max_retry_count = 1;
    if (retry_interval_ms < 0) retry_interval_ms = 0;
    if (decision_timeout_ms <= 0) decision_timeout_ms = 5000;
    if (scan_timeout_ms <= 0) scan_timeout_ms = 10000;
    request.scan_timeout_ms = scan_timeout_ms;

    if (wifi_nmcli_open(&device, interface_name[0] != '\0' ? interface_name : NULL) != 0) {
        send_report(fd, "wifi", "failed", 4103, "Unable to open Wi-Fi interface", "{}");
        return -1;
    }

    for (attempt = 1; attempt <= max_retry_count; ++attempt) {
        memset(&result, 0, sizeof(result));
        result.rssi = -127;
        if (wifi_nmcli_scan_signal(&device, &request, &result) != 0) {
            snprintf(data, sizeof(data),
                     "{\"ssid\":\"%s\",\"interfaceName\":\"%s\",\"attempt\":%d,\"maxRetryCount\":%d,"
                     "\"scanTimeoutMs\":%d,\"wifiEnabled\":%s,\"found\":false,\"rssi\":%d,\"scanRetryCount\":%d,"
                     "\"failureReason\":\"%s\"}",
                     ssid, device.interface_name, attempt, max_retry_count,
                     scan_timeout_ms, result.wifi_enabled ? "true" : "false", result.rssi,
                     result.scan_retry_count, result.failure_reason);
            wifi_nmcli_close(&device);
            send_report(fd, "wifi", "failed",
                        result.error_code == 0 ? 4101 : result.error_code,
                        result.error_message[0] == '\0' ? "Wi-Fi scan failed" : result.error_message,
                        data);
            return -1;
        }

        if (result.failure_reason[0] != '\0') {
            snprintf(data, sizeof(data),
                     "{\"ssid\":\"%s\",\"interfaceName\":\"%s\",\"phase\":\"scan_completed\",\"attempt\":%d,"
                     "\"maxRetryCount\":%d,\"scanTimeoutMs\":%d,\"readyForHostDecision\":true,"
                     "\"wifiEnabled\":%s,\"found\":%s,\"rssi\":%d,\"minRssi\":%d,"
                     "\"failureReason\":\"%s\"}",
                     ssid, device.interface_name, attempt, max_retry_count, scan_timeout_ms,
                     result.wifi_enabled ? "true" : "false",
                     result.found ? "true" : "false",
                     result.rssi, min_rssi, result.failure_reason);
        } else {
            snprintf(data, sizeof(data),
                     "{\"ssid\":\"%s\",\"interfaceName\":\"%s\",\"phase\":\"scan_completed\",\"attempt\":%d,"
                     "\"maxRetryCount\":%d,\"scanTimeoutMs\":%d,\"readyForHostDecision\":true,"
                     "\"wifiEnabled\":%s,\"found\":%s,\"rssi\":%d,\"minRssi\":%d}",
                     ssid, device.interface_name, attempt, max_retry_count, scan_timeout_ms,
                     result.wifi_enabled ? "true" : "false",
                     result.found ? "true" : "false",
                     result.rssi, min_rssi);
        }
        send_report(fd, "wifi", "running", 0, "Wi-Fi scan completed, waiting for host decision", data);

        switch (wait_test_decision(fd, "wifi", decision_timeout_ms, &decision_passed)) {
        case 1:
            if (decision_passed) {
                snprintf(data, sizeof(data),
                         "{\"ssid\":\"%s\",\"interfaceName\":\"%s\",\"phase\":\"completed\",\"attempt\":%d,"
                         "\"maxRetryCount\":%d,\"found\":%s,\"rssi\":%d,\"minRssi\":%d}",
                         ssid, device.interface_name, attempt, max_retry_count,
                         result.found ? "true" : "false", result.rssi, min_rssi);
                wifi_nmcli_close(&device);
                return send_report(fd, "wifi", "passed", 0, "Host confirmed Wi-Fi RSSI pass", data);
            }
            if (attempt < max_retry_count) {
                snprintf(data, sizeof(data),
                         "{\"ssid\":\"%s\",\"interfaceName\":\"%s\",\"phase\":\"retry_wait\",\"attempt\":%d,"
                         "\"maxRetryCount\":%d,\"retryIntervalMs\":%d,\"rssi\":%d,\"found\":%s}",
                         ssid, device.interface_name, attempt, max_retry_count, retry_interval_ms,
                         result.rssi, result.found ? "true" : "false");
                send_report(fd, "wifi", "running", 0, "Host requested Wi-Fi rescan", data);
                sleep_ms_local(retry_interval_ms);
                continue;
            }
            snprintf(data, sizeof(data),
                     "{\"ssid\":\"%s\",\"interfaceName\":\"%s\",\"phase\":\"completed\",\"attempt\":%d,"
                     "\"maxRetryCount\":%d,\"found\":%s,\"rssi\":%d,\"minRssi\":%d,"
                     "\"failureReason\":\"host_rejected\"}",
                     ssid, device.interface_name, attempt, max_retry_count,
                     result.found ? "true" : "false", result.rssi, min_rssi);
            wifi_nmcli_close(&device);
            send_report(fd, "wifi", "failed", 4106, "Host confirmed Wi-Fi RSSI fail", data);
            return -1;
        case 0:
            snprintf(data, sizeof(data),
                     "{\"ssid\":\"%s\",\"interfaceName\":\"%s\",\"phase\":\"decision_timeout\",\"attempt\":%d,"
                     "\"maxRetryCount\":%d,\"found\":%s,\"rssi\":%d,\"minRssi\":%d,"
                     "\"failureReason\":\"host_decision_timeout\"}",
                     ssid, device.interface_name, attempt, max_retry_count,
                     result.found ? "true" : "false", result.rssi, min_rssi);
            wifi_nmcli_close(&device);
            send_report(fd, "wifi", "failed", 4107, "Wi-Fi host decision timed out", data);
            return -1;
        default:
            snprintf(data, sizeof(data),
                     "{\"ssid\":\"%s\",\"interfaceName\":\"%s\",\"phase\":\"decision_failed\",\"attempt\":%d,"
                     "\"maxRetryCount\":%d,\"found\":%s,\"rssi\":%d,\"minRssi\":%d,"
                     "\"failureReason\":\"host_decision_read_failed\"}",
                     ssid, device.interface_name, attempt, max_retry_count,
                     result.found ? "true" : "false", result.rssi, min_rssi);
            wifi_nmcli_close(&device);
            send_report(fd, "wifi", "failed", 4108, "Unable to read Wi-Fi host decision", data);
            return -1;
        }
    }

    wifi_nmcli_close(&device);
    send_report(fd, "wifi", "failed", 4109, "Wi-Fi retry limit reached", "{}");
    return -1;
}

static int run_ethernet(int fd, const char *test_start, const char *test_end)
{
    char interface_name[64] = "end0";
    char router_ip[64] = "192.168.110.1";
    int wait_cable_timeout_ms = 30000;
    int progress_report_interval_ms = 1000;
    struct ethernet_request request = {
        .interface_name = interface_name,
        .router_ip = router_ip,
        .ping_count = 4,
        .timeout_ms = 15000,
    };
    struct ethernet_result result;
    char data[768];

    param_string(test_start, test_end, "interfaceName", interface_name, sizeof(interface_name));
    param_string(test_start, test_end, "routerIp", router_ip, sizeof(router_ip));
    request.ping_count = param_int(test_start, test_end, "pingCount", request.ping_count);
    request.timeout_ms = param_int(test_start, test_end, "timeoutMs", request.timeout_ms);
    wait_cable_timeout_ms = param_int(test_start, test_end, "waitCableTimeoutMs", wait_cable_timeout_ms);
    progress_report_interval_ms = param_int(test_start, test_end, "progressReportIntervalMs", progress_report_interval_ms);
    if (progress_report_interval_ms <= 0) progress_report_interval_ms = 1000;

    snprintf(data, sizeof(data),
             "{\"interfaceName\":\"%s\",\"routerIp\":\"%s\",\"phase\":\"wait_cable\","
             "\"ethernetLinkUp\":false,\"requiresCableInsert\":true,\"waitCableTimeoutMs\":%d,\"elapsedMs\":0}",
             interface_name, router_ip, wait_cable_timeout_ms);
    send_report(fd, "ethernet", "running", 0, "Insert Ethernet cable", data);

    if (!net_carrier_is_up(interface_name)) {
        int elapsed_ms = 0;
        while (elapsed_ms < wait_cable_timeout_ms && !net_carrier_is_up(interface_name)) {
            sleep_ms_local(progress_report_interval_ms);
            elapsed_ms += progress_report_interval_ms;
            snprintf(data, sizeof(data),
                     "{\"interfaceName\":\"%s\",\"routerIp\":\"%s\",\"phase\":\"wait_cable\","
                     "\"ethernetLinkUp\":false,\"requiresCableInsert\":true,\"waitCableTimeoutMs\":%d,\"elapsedMs\":%d}",
                     interface_name, router_ip, wait_cable_timeout_ms, elapsed_ms);
            send_report(fd, "ethernet", "running", 0, "Waiting for Ethernet cable", data);
        }
    }

    if (!net_carrier_is_up(interface_name)) {
        snprintf(data, sizeof(data),
                 "{\"interfaceName\":\"%s\",\"routerIp\":\"%s\",\"phase\":\"wait_cable\","
                 "\"ethernetLinkUp\":false,\"requiresCableInsert\":true,\"waitCableTimeoutMs\":%d,\"failureReason\":\"ethernet_insert_timeout\"}",
                 interface_name, router_ip, wait_cable_timeout_ms);
        send_report(fd, "ethernet", "failed", 4801, "Ethernet cable insert timeout", data);
        return -1;
    }

    snprintf(data, sizeof(data),
             "{\"interfaceName\":\"%s\",\"routerIp\":\"%s\",\"phase\":\"link_up\","
             "\"ethernetLinkUp\":true,\"requiresCableInsert\":false}",
             interface_name, router_ip);
    send_report(fd, "ethernet", "running", 0, "Ethernet cable detected", data);

    if (ethernet_nmcli_run_test(&request, &result) != 0) {
        snprintf(data, sizeof(data),
                 "{\"interfaceName\":\"%s\",\"routerIp\":\"%s\",\"phase\":\"failed\","
                 "\"ethernetLinkUp\":%s,\"ipAcquired\":%s,\"pingOk\":%s,\"ip\":\"%s\",\"failureReason\":\"%s\"}",
                 result.interface_name, result.router_ip,
                 result.link_up ? "true" : "false",
                 result.ip_acquired ? "true" : "false",
                 result.ping_ok ? "true" : "false",
                 result.ip,
                 result.failure_reason);
        send_report(fd, "ethernet", "failed",
                    result.error_code == 0 ? 4800 : result.error_code,
                    result.message[0] == '\0' ? "Ethernet test failed" : result.message,
                    data);
        return -1;
    }

    snprintf(data, sizeof(data),
             "{\"interfaceName\":\"%s\",\"ip\":\"%s\",\"routerIp\":\"%s\",\"pingCount\":%d,\"avgDelayMs\":%d,"
             "\"pingOk\":true,\"phase\":\"completed\",\"ethernetLinkUp\":true}",
             result.interface_name, result.ip, result.router_ip,
             result.completed_ping_count, result.avg_delay_ms);
    return send_report(fd, "ethernet", "passed", 0, result.message, data);
}

static int run_tf_card(int fd, const struct app_config *config, const char *test_start, const char *test_end)
{
    char device_path[128];
    char mount_point[160];
    struct tf_card_request request = {
        .device_path = device_path,
        .mount_point = mount_point,
        .allow_format_ext4 = config->tf_allow_format_ext4 != 0,
        .min_capacity_mb = 0,
    };
    struct tf_card_result result;
    char data[768];

    snprintf(device_path, sizeof(device_path), "%s", config->tf_device_path);
    snprintf(mount_point, sizeof(mount_point), "%s", config->tf_mount_point);
    param_string(test_start, test_end, "devicePath", device_path, sizeof(device_path));
    param_string(test_start, test_end, "mountPoint", mount_point, sizeof(mount_point));
    request.allow_format_ext4 = param_bool(test_start, test_end, "allowFormatExt4", request.allow_format_ext4);
    request.min_capacity_mb = param_int(test_start, test_end, "minCapacityMb", request.min_capacity_mb);
    send_report(fd, "tf", "running", 0, "Running TF card test", "{}");
    if (tf_card_run_test(&request, &result) != 0) {
        send_report(fd, "tf", "failed",
                    result.error_code == 0 ? 4300 : result.error_code,
                    result.message, "{}");
        return -1;
    }
    snprintf(data, sizeof(data),
             "{\"device\":\"%s\",\"filesystem\":\"%s\",\"mountPoint\":\"%s\",\"formatted\":%s,\"totalMb\":%llu,\"freeMb\":%llu,\"rwPassed\":%s}",
             result.device_path, result.filesystem, result.mount_point,
             result.formatted ? "true" : "false",
             (unsigned long long)result.total_mb,
             (unsigned long long)result.free_mb,
             result.rw_passed ? "true" : "false");
    return send_report(fd, "tf", "passed", 0, result.message, data);
}

static int usb_speed_matches(int usb_version, int speed_mbps)
{
    return usb_version == 2 ? speed_mbps > 0 && speed_mbps < 5000 : speed_mbps >= 5000;
}

static int wait_for_usb_absent(int fd, const char *test_id, const char *phase,
                               const char *port_name, const char *direction,
                               int step_index, int timeout_ms, int poll_interval_ms)
{
    int elapsed_ms = 0;
    char data[512];
    struct usb_insert_device device;
    while (elapsed_ms <= timeout_ms) {
        int found = usb_insert_find(NULL, &device);
        if (found == 0) return 0;
        if (found < 0) return -1;
        if (elapsed_ms == 0 || elapsed_ms % 1000 == 0) {
            snprintf(data, sizeof(data),
                     "{\"phase\":\"%s\",\"port\":\"%s\",\"direction\":\"%s\","
                     "\"stepIndex\":%d,\"totalSteps\":4,\"elapsedMs\":%d,\"timeoutMs\":%d,"
                     "\"detectedBlock\":\"%s\",\"detectedTopology\":\"%s\",\"actualSpeedMbps\":%d}",
                     phase, port_name, direction, step_index, elapsed_ms, timeout_ms,
                     device.block_name, device.topology, device.speed_mbps);
            send_report(fd, test_id, "running", 0, "Remove USB storage device before continuing", data);
        }
        sleep_ms_local(poll_interval_ms);
        elapsed_ms += poll_interval_ms;
    }
    return 1;
}

static int run_usb_variant(int fd, const char *test_start, const char *test_end, int usb_version)
{
    const char *test_id = usb_version == 2 ? "usb2" : "usb3";
    char port1_topology[64] = "";
    char port2_topology[64] = "";
    char data[768];
    int insert_timeout_ms = param_int(test_start, test_end, "insertTimeoutMs", 30000);
    int remove_timeout_ms = param_int(test_start, test_end, "removeTimeoutMs", 15000);
    int poll_interval_ms = param_int(test_start, test_end, "pollIntervalMs", 250);
    int step;

    param_string(test_start, test_end, "port1Topology", port1_topology, sizeof(port1_topology));
    param_string(test_start, test_end, "port2Topology", port2_topology, sizeof(port2_topology));
    if (insert_timeout_ms < 1000) insert_timeout_ms = 30000;
    if (remove_timeout_ms < 1000) remove_timeout_ms = 15000;
    if (poll_interval_ms < 100) poll_interval_ms = 100;

    if (wait_for_usb_absent(fd, test_id, "wait_remove_before_start", "", "", 0,
                            remove_timeout_ms, poll_interval_ms) != 0) {
        send_report(fd, test_id, "failed", 4903,
                    "USB storage device was not removed before test start",
                    "{\"phase\":\"wait_remove_before_start\"}");
        return -1;
    }

    for (step = 0; step < 4; ++step) {
        const char *port_name = step < 2 ? "port1" : "port2";
        const char *direction = step % 2 == 0 ? "normal" : "reverse";
        char *topology = step < 2 ? port1_topology : port2_topology;
        struct usb_insert_device device;
        int elapsed_ms = 0;
        int found = 0;

        snprintf(data, sizeof(data),
                 "{\"phase\":\"wait_insert\",\"usbVersion\":\"usb%d\",\"port\":\"%s\","
                 "\"direction\":\"%s\",\"stepIndex\":%d,\"totalSteps\":4,"
                 "\"expectedTopology\":\"%s\",\"insertTimeoutMs\":%d}",
                 usb_version, port_name, direction, step + 1, topology, insert_timeout_ms);
        send_report(fd, test_id, "running", 0, "Insert USB storage device", data);

        while (elapsed_ms <= insert_timeout_ms) {
            found = usb_insert_find(NULL, &device);
            if (found != 0) break;
            sleep_ms_local(poll_interval_ms);
            elapsed_ms += poll_interval_ms;
        }
        if (found < 0) {
            send_report(fd, test_id, "failed", 4904,
                        "Unable to scan USB storage devices", "{\"phase\":\"wait_insert\"}");
            return -1;
        }
        if (found == 0) {
            snprintf(data, sizeof(data),
                     "{\"phase\":\"wait_insert\",\"port\":\"%s\",\"direction\":\"%s\","
                     "\"stepIndex\":%d,\"totalSteps\":4,\"timeoutMs\":%d}",
                     port_name, direction, step + 1, insert_timeout_ms);
            send_report(fd, test_id, "failed", 4905, "USB insertion timed out", data);
            return -1;
        }
        if (device.topology[0] == '\0') {
            send_report(fd, test_id, "failed", 4904,
                        "Unable to resolve USB physical topology",
                        "{\"phase\":\"detected\",\"failureReason\":\"topology_unavailable\"}");
            return -1;
        }
        if (topology[0] == '\0') {
            if (step == 2 && port1_topology[0] != '\0' &&
                strcmp(device.topology, port1_topology) == 0) {
                snprintf(data, sizeof(data),
                         "{\"phase\":\"detected\",\"port\":\"%s\",\"direction\":\"%s\","
                         "\"stepIndex\":%d,\"port1Topology\":\"%s\",\"actualTopology\":\"%s\"}",
                         port_name, direction, step + 1, port1_topology, device.topology);
                send_report(fd, test_id, "failed", 4906,
                            "USB port2 must use a different physical topology from port1", data);
                return -1;
            }
            snprintf(topology, 64, "%s", device.topology);
        } else if (strcmp(device.topology, topology) != 0) {
            snprintf(data, sizeof(data),
                     "{\"phase\":\"detected\",\"port\":\"%s\",\"direction\":\"%s\","
                     "\"stepIndex\":%d,\"expectedTopology\":\"%s\",\"actualTopology\":\"%s\","
                     "\"actualSpeedMbps\":%d}",
                     port_name, direction, step + 1, topology, device.topology, device.speed_mbps);
            send_report(fd, test_id, "failed", 4906, "USB device was inserted into the wrong port", data);
            return -1;
        }
        if (!usb_speed_matches(usb_version, device.speed_mbps)) {
            snprintf(data, sizeof(data),
                     "{\"phase\":\"detected\",\"port\":\"%s\",\"direction\":\"%s\","
                     "\"stepIndex\":%d,\"usbVersion\":\"usb%d\",\"actualSpeedMbps\":%d,"
                     "\"detectedTopology\":\"%s\"}",
                     port_name, direction, step + 1, usb_version, device.speed_mbps, device.topology);
            send_report(fd, test_id, "failed", 4907, "USB link speed does not match the current test", data);
            return -1;
        }

        snprintf(data, sizeof(data),
                 "{\"phase\":\"detected\",\"port\":\"%s\",\"direction\":\"%s\","
                 "\"stepIndex\":%d,\"totalSteps\":4,\"usbVersion\":\"usb%d\","
                 "\"actualSpeedMbps\":%d,\"detectedBlock\":\"%s\",\"detectedTopology\":\"%s\","
                 "\"speedPassed\":true}",
                 port_name, direction, step + 1, usb_version, device.speed_mbps,
                 device.block_name, device.topology);
        send_report(fd, test_id, "running", 0, "USB insertion and link speed detected", data);

        if (wait_for_usb_absent(fd, test_id, "wait_remove", port_name, direction, step + 1,
                                remove_timeout_ms, poll_interval_ms) != 0) {
            snprintf(data, sizeof(data),
                     "{\"phase\":\"wait_remove\",\"port\":\"%s\",\"direction\":\"%s\","
                     "\"stepIndex\":%d,\"totalSteps\":4,\"timeoutMs\":%d}",
                     port_name, direction, step + 1, remove_timeout_ms);
            send_report(fd, test_id, "failed", 4908, "USB removal timed out", data);
            return -1;
        }
    }

    snprintf(data, sizeof(data),
             "{\"phase\":\"completed\",\"usbVersion\":\"usb%d\",\"completedSteps\":4,"
             "\"port1Normal\":true,\"port1Reverse\":true,\"port2Normal\":true,\"port2Reverse\":true}",
             usb_version);
    return send_report(fd, test_id, "passed", 0,
                       usb_version == 2 ? "USB2.0 four-step insertion test passed" :
                                          "USB3.0 four-step insertion test passed",
                       data);
}

static int run_usb2_3(int fd, const char *test_start, const char *test_end)
{
    int rc = run_usb_variant(fd, test_start, test_end, 2);
    if (rc != 0) return rc;
    return run_usb_variant(fd, test_start, test_end, 3);
}

static void append_pcba_points_json(char *data, size_t data_size,
                                    const struct pcba_points_result *result,
                                    int include_all_points)
{
    size_t used;
    int i;
    snprintf(data, data_size,
             "{\"recordFile\":\"%s\",\"channelCount\":%d,\"parsedCount\":%d,\"passedCount\":%d,\"failedCount\":%d,\"failedPoints\":[",
             result->record_file, result->channel_count, result->parsed_count,
             result->passed_count, result->failed_count);
    used = strnlen(data, data_size);
    for (i = 0; i < result->parsed_count && i < 32; ++i) {
        if (!result->points[i].passed) {
            snprintf(data + used, data_size - used, "%s%d", used > 0 && data[used - 1] != '[' ? "," : "", result->points[i].index);
            used = strnlen(data, data_size);
        }
    }
    snprintf(data + used, data_size - used, "],\"points\":[");
    used = strnlen(data, data_size);
    if (include_all_points) {
        for (i = 0; i < result->parsed_count && i < 32; ++i) {
            snprintf(data + used, data_size - used,
                     "%s{\"index\":%d,\"name\":\"TP%02d\",\"voltageMv\":%d,\"minMv\":%d,\"maxMv\":%d,\"passed\":%s}",
                     i == 0 ? "" : ",",
                     result->points[i].index, result->points[i].index,
                     result->points[i].voltage_mv, result->points[i].min_mv,
                     result->points[i].max_mv, result->points[i].passed ? "true" : "false");
            used = strnlen(data, data_size);
        }
    }
    snprintf(data + used, data_size - used, "]}");
}

static int run_pcba_test_points(int fd, const char *test_start, const char *test_end)
{
    char record_file[160] = "/tmp/spacetest_pcba_points.json";
    struct pcba_points_request request = {
        .record_file = record_file,
        .channel_count = 32,
        .default_min_mv = 0,
        .default_max_mv = 5000,
        .timeout_ms = 5000
    };
    struct pcba_points_result result;
    char data[8192];

    param_string(test_start, test_end, "recordFile", record_file, sizeof(record_file));
    request.channel_count = param_int(test_start, test_end, "channelCount", request.channel_count);
    request.default_min_mv = param_int(test_start, test_end, "defaultMinMv", request.default_min_mv);
    request.default_max_mv = param_int(test_start, test_end, "defaultMaxMv", request.default_max_mv);
    request.timeout_ms = param_int(test_start, test_end, "timeoutMs", request.timeout_ms);

    snprintf(data, sizeof(data),
             "{\"recordFile\":\"%s\",\"channelCount\":%d,\"defaultMinMv\":%d,\"defaultMaxMv\":%d}",
             record_file, request.channel_count, request.default_min_mv, request.default_max_mv);
    send_report(fd, "pcba_test_points", "running", 0, "Read PCBA test point voltages", data);

    if (pcba_points_run_test(&request, &result) != 0) {
        append_pcba_points_json(data, sizeof(data), &result, 1);
        send_report(fd, "pcba_test_points", "failed",
                    result.error_code == 0 ? 5000 : result.error_code,
                    result.message[0] == '\0' ? "PCBA test point check failed" : result.message,
                    data);
        return -1;
    }

    append_pcba_points_json(data, sizeof(data), &result, 1);
    return send_report(fd, "pcba_test_points", "passed", 0, result.message, data);
}

static int read_text_file_trimmed(const char *path, char *buffer, size_t buffer_size)
{
    FILE *file;
    size_t length;
    if (path == NULL || buffer == NULL || buffer_size == 0) return -1;
    buffer[0] = '\0';
    file = fopen(path, "r");
    if (file == NULL) return -1;
    if (fgets(buffer, (int)buffer_size, file) == NULL) {
        fclose(file);
        return -1;
    }
    fclose(file);
    length = strcspn(buffer, "\r\n");
    buffer[length] = '\0';
    return buffer[0] == '\0' ? -1 : 0;
}

static int read_ull_file(const char *path, unsigned long long *value)
{
    FILE *file;
    unsigned long long parsed;
    if (path == NULL || value == NULL) return -1;
    file = fopen(path, "r");
    if (file == NULL) return -1;
    if (fscanf(file, "%llu", &parsed) != 1) {
        fclose(file);
        return -1;
    }
    fclose(file);
    *value = parsed;
    return 0;
}

static unsigned char emmc_test_byte(unsigned long long offset)
{
    return (unsigned char)(((offset * 31ULL) + 17ULL) & 0xFFULL);
}

static int ensure_directory_for_test(const char *path)
{
    struct stat st;
    if (path == NULL || path[0] == '\0') return -1;
    if (stat(path, &st) == 0) return S_ISDIR(st.st_mode) ? 0 : -1;
    return mkdir(path, 0775);
}

static int run_emmc_file_pattern_test(const char *directory, int file_mib,
                                      char *test_file, size_t test_file_size,
                                      char *failure_reason, size_t failure_reason_size)
{
    const size_t chunk_size = 1024U * 1024U;
    unsigned char *buffer;
    int fd;
    int chunk;
    int total_chunks;
    ssize_t io_count;
    size_t i;
    unsigned long long base_offset;

    if (test_file == NULL || test_file_size == 0 || failure_reason == NULL || failure_reason_size == 0) return -1;
    test_file[0] = '\0';
    failure_reason[0] = '\0';
    if (file_mib <= 0) file_mib = 64;
    if (file_mib > 512) file_mib = 512;
    if (ensure_directory_for_test(directory) != 0) {
        snprintf(failure_reason, failure_reason_size, "emmc_test_directory_unavailable");
        return -1;
    }
    snprintf(test_file, test_file_size, "%s/spacetest_emmc_ddr_rw.bin", directory);
    buffer = (unsigned char *)malloc(chunk_size);
    if (buffer == NULL) {
        snprintf(failure_reason, failure_reason_size, "emmc_buffer_allocate_failed");
        return -1;
    }

    fd = open(test_file, O_CREAT | O_TRUNC | O_RDWR | O_CLOEXEC, 0664);
    if (fd < 0) {
        free(buffer);
        snprintf(failure_reason, failure_reason_size, "emmc_open_failed");
        return -1;
    }

    total_chunks = file_mib;
    for (chunk = 0; chunk < total_chunks; ++chunk) {
        base_offset = (unsigned long long)chunk * (unsigned long long)chunk_size;
        for (i = 0; i < chunk_size; ++i) buffer[i] = emmc_test_byte(base_offset + (unsigned long long)i);
        io_count = write(fd, buffer, chunk_size);
        if (io_count != (ssize_t)chunk_size) {
            close(fd);
            unlink(test_file);
            free(buffer);
            snprintf(failure_reason, failure_reason_size, "emmc_write_failed");
            return -1;
        }
    }
    if (fsync(fd) != 0 || lseek(fd, 0, SEEK_SET) < 0) {
        close(fd);
        unlink(test_file);
        free(buffer);
        snprintf(failure_reason, failure_reason_size, "emmc_sync_or_seek_failed");
        return -1;
    }

    for (chunk = 0; chunk < total_chunks; ++chunk) {
        base_offset = (unsigned long long)chunk * (unsigned long long)chunk_size;
        io_count = read(fd, buffer, chunk_size);
        if (io_count != (ssize_t)chunk_size) {
            close(fd);
            unlink(test_file);
            free(buffer);
            snprintf(failure_reason, failure_reason_size, "emmc_read_failed");
            return -1;
        }
        for (i = 0; i < chunk_size; ++i) {
            if (buffer[i] != emmc_test_byte(base_offset + (unsigned long long)i)) {
                close(fd);
                unlink(test_file);
                free(buffer);
                snprintf(failure_reason, failure_reason_size, "emmc_verify_failed");
                return -1;
            }
        }
    }

    close(fd);
    if (unlink(test_file) != 0) {
        free(buffer);
        snprintf(failure_reason, failure_reason_size, "emmc_cleanup_failed");
        return -1;
    }
    free(buffer);
    return 0;
}

static int read_memtotal_mib(unsigned long long *memtotal_mib)
{
    FILE *file;
    char key[64];
    unsigned long long kb;
    char unit[32];
    if (memtotal_mib == NULL) return -1;
    file = fopen("/proc/meminfo", "r");
    if (file == NULL) return -1;
    while (fscanf(file, "%63s %llu %31s", key, &kb, unit) == 3) {
        if (strcmp(key, "MemTotal:") == 0) {
            fclose(file);
            *memtotal_mib = kb / 1024ULL;
            return 0;
        }
    }
    fclose(file);
    return -1;
}

static int run_ddr_pattern_test(int stress_mib, int loops, char *failure_reason, size_t failure_reason_size)
{
    size_t total_bytes;
    uint32_t *words;
    size_t word_count;
    int loop;
    int pattern_index;
    static const uint32_t patterns[] = { 0x00000000U, 0xFFFFFFFFU, 0xAA55AA55U, 0x55AA55AAU };
    size_t i;
    uint32_t expected;

    if (failure_reason == NULL || failure_reason_size == 0) return -1;
    failure_reason[0] = '\0';
    if (stress_mib <= 0) stress_mib = 256;
    if (stress_mib > 1024) stress_mib = 1024;
    if (loops <= 0) loops = 2;
    if (loops > 10) loops = 10;
    total_bytes = (size_t)stress_mib * 1024U * 1024U;
    words = (uint32_t *)malloc(total_bytes);
    if (words == NULL) {
        snprintf(failure_reason, failure_reason_size, "ddr_allocate_failed");
        return -1;
    }
    word_count = total_bytes / sizeof(uint32_t);

    for (loop = 0; loop < loops; ++loop) {
        for (pattern_index = 0; pattern_index < (int)(sizeof(patterns) / sizeof(patterns[0])); ++pattern_index) {
            for (i = 0; i < word_count; ++i) words[i] = patterns[pattern_index] ^ (uint32_t)i;
            for (i = 0; i < word_count; ++i) {
                expected = patterns[pattern_index] ^ (uint32_t)i;
                if (words[i] != expected) {
                    free(words);
                    snprintf(failure_reason, failure_reason_size, "ddr_pattern_mismatch");
                    return -1;
                }
            }
        }
    }

    free(words);
    return 0;
}

static int run_emmc_ddr(int fd, const char *test_id, const char *test_start, const char *test_end)
{
    char emmc_device[64] = "mmcblk0";
    char emmc_test_dir[160] = "/userdata/factory_test";
    char path[192];
    char emmc_name[128] = "";
    char emmc_cid[256] = "";
    char emmc_manfid[64] = "";
    char emmc_test_file[192] = "";
    char failure_reason[96] = "";
    char data[2048];
    unsigned long long emmc_sectors = 0;
    unsigned long long emmc_capacity_mib = 0;
    unsigned long long emmc_min_capacity_mib = 115ULL * 1024ULL;
    int emmc_test_file_mib = 64;
    unsigned long long ddr_memtotal_mib = 0;
    int ddr_min_memtotal_mib = 3200;
    int ddr_stress_mib = 256;
    int ddr_stress_loops = 2;
    struct timespec ddr_start_ts;
    struct timespec ddr_end_ts;
    double ddr_elapsed_ms = 0.0;
    double ddr_throughput_mib_per_sec = 0.0;
    unsigned long long ddr_processed_mib = 0;

    param_string(test_start, test_end, "emmcDevice", emmc_device, sizeof(emmc_device));
    param_string(test_start, test_end, "emmcTestDirectory", emmc_test_dir, sizeof(emmc_test_dir));
    emmc_min_capacity_mib = (unsigned long long)param_int(test_start, test_end, "emmcMinCapacityGiB", 115) * 1024ULL;
    emmc_test_file_mib = param_int(test_start, test_end, "emmcTestFileMiB", emmc_test_file_mib);
    ddr_min_memtotal_mib = param_int(test_start, test_end, "ddrMinMemTotalMiB", ddr_min_memtotal_mib);
    ddr_stress_mib = param_int(test_start, test_end, "ddrStressMiB", ddr_stress_mib);
    ddr_stress_loops = param_int(test_start, test_end, "ddrStressLoops", ddr_stress_loops);

    snprintf(data, sizeof(data),
             "{\"phase\":\"start\",\"emmcDevice\":\"%s\",\"emmcMinCapacityMiB\":%llu,"
             "\"emmcTestFileMiB\":%d,\"ddrMinMemTotalMiB\":%d,\"ddrStressMiB\":%d,\"ddrStressLoops\":%d}",
             emmc_device, emmc_min_capacity_mib, emmc_test_file_mib,
             ddr_min_memtotal_mib, ddr_stress_mib, ddr_stress_loops);
    send_report(fd, test_id, "running", 0, strcmp(test_id, "emmc") == 0 ? "Running eMMC device test" : "Running DDR device test", data);

    if (strcmp(test_id, "ddr") == 0) goto ddr_start;

    snprintf(path, sizeof(path), "/sys/block/%s/size", emmc_device);
    if (read_ull_file(path, &emmc_sectors) != 0) {
        snprintf(data, sizeof(data), "{\"phase\":\"emmc_info\",\"emmcDevice\":\"%s\",\"failureReason\":\"emmc_device_not_found\"}", emmc_device);
        send_report(fd, test_id, "failed", 5101, "eMMC block device was not found", data);
        return -1;
    }
    emmc_capacity_mib = emmc_sectors / 2048ULL;
    snprintf(path, sizeof(path), "/sys/block/%s/device/name", emmc_device);
    (void)read_text_file_trimmed(path, emmc_name, sizeof(emmc_name));
    snprintf(path, sizeof(path), "/sys/block/%s/device/cid", emmc_device);
    (void)read_text_file_trimmed(path, emmc_cid, sizeof(emmc_cid));
    snprintf(path, sizeof(path), "/sys/block/%s/device/manfid", emmc_device);
    (void)read_text_file_trimmed(path, emmc_manfid, sizeof(emmc_manfid));

    if (emmc_capacity_mib < emmc_min_capacity_mib) {
        snprintf(data, sizeof(data),
                 "{\"phase\":\"emmc_capacity\",\"emmcDevice\":\"%s\",\"emmcName\":\"%s\",\"emmcCid\":\"%s\","
                 "\"emmcManfid\":\"%s\",\"emmcCapacityMiB\":%llu,\"emmcMinCapacityMiB\":%llu,"
                 "\"failureReason\":\"emmc_capacity_too_small\"}",
                 emmc_device, emmc_name, emmc_cid, emmc_manfid, emmc_capacity_mib, emmc_min_capacity_mib);
        send_report(fd, test_id, "failed", 5102, "eMMC capacity is below threshold", data);
        return -1;
    }

    if (run_emmc_file_pattern_test(emmc_test_dir, emmc_test_file_mib,
                                   emmc_test_file, sizeof(emmc_test_file),
                                   failure_reason, sizeof(failure_reason)) != 0) {
        snprintf(data, sizeof(data),
                 "{\"phase\":\"emmc_rw\",\"emmcDevice\":\"%s\",\"emmcName\":\"%s\",\"emmcCapacityMiB\":%llu,"
                 "\"emmcTestFile\":\"%s\",\"emmcTestFileMiB\":%d,\"failureReason\":\"%s\"}",
                 emmc_device, emmc_name, emmc_capacity_mib, emmc_test_file, emmc_test_file_mib, failure_reason);
        send_report(fd, test_id, "failed", 5103, "eMMC read/write verify failed", data);
        return -1;
    }

    if (strcmp(test_id, "emmc") == 0) {
        snprintf(data, sizeof(data), "{\"phase\":\"completed\",\"emmcDevice\":\"%s\",\"emmcName\":\"%s\",\"emmcCapacityMiB\":%llu,\"emmcTestFileMiB\":%d}", emmc_device, emmc_name, emmc_capacity_mib, emmc_test_file_mib);
        return send_report(fd, test_id, "passed", 0, "eMMC device test passed", data);
    }

ddr_start:
    if (read_memtotal_mib(&ddr_memtotal_mib) != 0) {
        snprintf(data, sizeof(data), "{\"phase\":\"ddr_info\",\"failureReason\":\"ddr_meminfo_unavailable\"}");
        send_report(fd, test_id, "failed", 5111, "DDR memory information is unavailable", data);
        return -1;
    }
    if (ddr_memtotal_mib < (unsigned long long)ddr_min_memtotal_mib) {
        snprintf(data, sizeof(data),
                 "{\"phase\":\"ddr_capacity\",\"ddrMemTotalMiB\":%llu,\"ddrMinMemTotalMiB\":%d,"
                 "\"failureReason\":\"ddr_capacity_too_small\"}",
                 ddr_memtotal_mib, ddr_min_memtotal_mib);
        send_report(fd, test_id, "failed", 5112, "DDR capacity is below threshold", data);
        return -1;
    }

    if (clock_gettime(CLOCK_MONOTONIC, &ddr_start_ts) != 0) {
        ddr_start_ts.tv_sec = 0;
        ddr_start_ts.tv_nsec = 0;
    }
    if (run_ddr_pattern_test(ddr_stress_mib, ddr_stress_loops, failure_reason, sizeof(failure_reason)) != 0) {
        snprintf(data, sizeof(data),
                 "{\"phase\":\"ddr_stress\",\"ddrMemTotalMiB\":%llu,\"ddrStressMiB\":%d,"
                 "\"ddrStressLoops\":%d,\"failureReason\":\"%s\"}",
                 ddr_memtotal_mib, ddr_stress_mib, ddr_stress_loops, failure_reason);
        send_report(fd, test_id, "failed", 5113, "DDR pattern stress test failed", data);
        return -1;
    }
    if (clock_gettime(CLOCK_MONOTONIC, &ddr_end_ts) == 0 && ddr_start_ts.tv_sec != 0) {
        ddr_elapsed_ms = timespec_diff_ms(&ddr_start_ts, &ddr_end_ts);
        ddr_processed_mib = (unsigned long long)ddr_stress_mib * (unsigned long long)ddr_stress_loops * 8ULL;
        if (ddr_elapsed_ms > 0.0) {
            ddr_throughput_mib_per_sec = ((double)ddr_processed_mib * 1000.0) / ddr_elapsed_ms;
        }
    }

    snprintf(data, sizeof(data),
             "{\"phase\":\"completed\",\"emmcDevice\":\"%s\",\"emmcName\":\"%s\",\"emmcCid\":\"%s\","
             "\"emmcManfid\":\"%s\",\"emmcCapacityMiB\":%llu,\"emmcMinCapacityMiB\":%llu,"
             "\"emmcTestFileMiB\":%d,\"ddrMemTotalMiB\":%llu,\"ddrMinMemTotalMiB\":%d,"
             "\"ddrStressMiB\":%d,\"ddrStressLoops\":%d,\"ddrProcessedMiB\":%llu,"
             "\"ddrElapsedMs\":%.2f,\"ddrThroughputMiBPerSec\":%.2f,\"ddrPatternPass\":true}",
             emmc_device, emmc_name, emmc_cid, emmc_manfid,
             emmc_capacity_mib, emmc_min_capacity_mib, emmc_test_file_mib,
             ddr_memtotal_mib, ddr_min_memtotal_mib, ddr_stress_mib, ddr_stress_loops,
             ddr_processed_mib, ddr_elapsed_ms, ddr_throughput_mib_per_sec);
    return send_report(fd, test_id, "passed", 0, "DDR device test passed", data);
}

static int run_bluetooth(int fd, const struct app_config *config, const char *test_start, const char *test_end)
{
    char target_name[128];
    int max_retry_count = 5;
    int retry_interval_ms = 2000;
    int attempt;
    struct bluetooth_request request = {
        .target_name = target_name,
        .timeout_ms = 8000,
        .min_rssi = config->bluetooth_min_rssi,
    };
    struct bluetooth_result result;
    char data[1024];

    /*
     * The upper PC configures its BLE broadcaster name and sends that exact
     * name as bluetooth.parameters.targetName.  Keep this fallback only for
     * local smoke tests; production should not hard-code a target here.
     */
    snprintf(target_name, sizeof(target_name), "%s", config->bluetooth_target_name);
    param_string(test_start, test_end, "targetName", target_name, sizeof(target_name));
    request.min_rssi = param_int(test_start, test_end, "minRssi", request.min_rssi);
    request.timeout_ms = param_int(test_start, test_end, "scanWindowMs", request.timeout_ms);
    request.timeout_ms = param_int(test_start, test_end, "timeoutMs", request.timeout_ms);
    max_retry_count = param_int(test_start, test_end, "maxRetryCount", max_retry_count);
    retry_interval_ms = param_int(test_start, test_end, "retryIntervalMs", retry_interval_ms);
    if (max_retry_count <= 0) max_retry_count = 1;
    if (retry_interval_ms < 0) retry_interval_ms = 0;

    for (attempt = 1; attempt <= max_retry_count; ++attempt) {
        memset(&result, 0, sizeof(result));
        snprintf(data, sizeof(data),
                 "{\"targetName\":\"%s\",\"minRssi\":%d,\"scanWindowMs\":%d,\"phase\":\"scan_started\","
                 "\"attempt\":%d,\"maxRetryCount\":%d}",
                 target_name, request.min_rssi, request.timeout_ms, attempt, max_retry_count);
        send_report(fd, "bluetooth", "running", 0, "Running Bluetooth scan", data);

        if (bluetoothctl_scan_target(&request, &result) == 0) {
            snprintf(data, sizeof(data),
                     "{\"targetName\":\"%s\",\"name\":\"%s\",\"mac\":\"%s\",\"rssi\":%d,\"minRssi\":%d,"
                     "\"scanWindowMs\":%d,\"attempt\":%d,\"maxRetryCount\":%d,"
                     "\"bestSeenName\":\"%s\",\"bestSeenMac\":\"%s\",\"bestSeenRssi\":%d}",
                     target_name, result.name, result.mac, result.rssi, request.min_rssi,
                     request.timeout_ms, attempt, max_retry_count,
                     result.best_seen_name, result.best_seen_mac, result.best_seen_rssi);
            return send_report(fd, "bluetooth", "passed", 0, "Bluetooth target found", data);
        }

        if (attempt < max_retry_count) {
            snprintf(data, sizeof(data),
                     "{\"targetName\":\"%s\",\"minRssi\":%d,\"scanWindowMs\":%d,\"phase\":\"retry_wait\","
                     "\"attempt\":%d,\"maxRetryCount\":%d,\"retryIntervalMs\":%d,\"found\":%s,"
                     "\"matchedName\":\"%s\",\"matchedMac\":\"%s\",\"matchedRssi\":%d,"
                     "\"bestSeenName\":\"%s\",\"bestSeenMac\":\"%s\",\"bestSeenRssi\":%d,"
                     "\"failureReason\":\"%s\"}",
                     target_name, request.min_rssi, request.timeout_ms,
                     attempt, max_retry_count, retry_interval_ms, result.found ? "true" : "false",
                     result.name, result.mac, result.matched_rssi,
                     result.best_seen_name, result.best_seen_mac, result.best_seen_rssi,
                     result.failure_reason);
            send_report(fd, "bluetooth", "running", 0, "Bluetooth scan failed, waiting to retry", data);
            sleep_ms_local(retry_interval_ms);
            continue;
        }

        snprintf(data, sizeof(data),
                 "{\"targetName\":\"%s\",\"minRssi\":%d,\"scanWindowMs\":%d,\"attempt\":%d,\"maxRetryCount\":%d,\"found\":%s,"
                 "\"matchedName\":\"%s\",\"matchedMac\":\"%s\",\"matchedRssi\":%d,"
                 "\"bestSeenName\":\"%s\",\"bestSeenMac\":\"%s\",\"bestSeenRssi\":%d,"
                 "\"failureReason\":\"%s\"}",
                 target_name, request.min_rssi, request.timeout_ms,
                 attempt, max_retry_count, result.found ? "true" : "false",
                 result.name, result.mac, result.matched_rssi,
                 result.best_seen_name, result.best_seen_mac, result.best_seen_rssi,
                 result.failure_reason);
        send_report(fd, "bluetooth", "failed",
                    result.error_code == 0 ? 4200 : result.error_code,
                    result.error_message[0] == '\0' ? "Bluetooth test failed" : result.error_message,
                    data);
        return -1;
    }

    return -1;
}

static int run_fast_charge(int fd, const struct app_config *config, const char *test_start, const char *test_end)
{
    struct fast_charge_device device;
    struct fast_charge_request request = {
        .voltage_min_mv = config->fast_charge_voltage_min_mv,
        .voltage_max_mv = config->fast_charge_voltage_max_mv,
        .current_min_ma = config->fast_charge_current_min_ma,
        .current_max_ma = config->fast_charge_current_max_ma,
        .stable_sample_count = 1,
        .sample_interval_ms = 200,
        .timeout_ms = 1000,
    };
    struct fast_charge_result result;
    char data[1024];
    int manual_insert_wait_ms = 30000;
    int wait_charger_timeout_ms = 30000;
    int wait_ready_timeout_ms = 120000;
    int progress_report_interval_ms = 1000;
    int elapsed_ms = 0;
    int ready_elapsed_ms = 0;
    int pmic_status0 = 0;
    int pmic_status1 = 0;
    int vbus_present = 0;
    int pg_stat = 0;
    int chg_stat = 0;
    int vbus_stat = 0;
    int bc12_done = 0;
    int charger_detected = 0;
    int last_known_charging = 0;
    int last_known_charge_stage = 0;
    int last_known_pmic_status0 = 0;
    int last_known_pmic_status1 = 0;
    int last_known_vbus_stat = 0;
    int last_known_bc12_done = 0;
    int passed = 0;

    request.voltage_min_mv = param_int(test_start, test_end, "chargeVoltageMinMv", request.voltage_min_mv);
    request.voltage_max_mv = param_int(test_start, test_end, "chargeVoltageMaxMv", request.voltage_max_mv);
    request.current_min_ma = param_int(test_start, test_end, "chargeCurrentMinMa", request.current_min_ma);
    request.current_max_ma = param_int(test_start, test_end, "chargeCurrentMaxMa", request.current_max_ma);
    request.stable_sample_count = param_int(test_start, test_end, "stableSampleCount", request.stable_sample_count);
    request.sample_interval_ms = param_int(test_start, test_end, "sampleIntervalMs", request.sample_interval_ms);
    request.timeout_ms = param_int(test_start, test_end, "timeoutMs", request.timeout_ms);
    manual_insert_wait_ms = param_int(test_start, test_end, "manualInsertWaitMs", manual_insert_wait_ms);
    wait_ready_timeout_ms = param_int(test_start, test_end, "waitReadyTimeoutMs", wait_ready_timeout_ms);
    wait_charger_timeout_ms = param_int(test_start, test_end, "waitChargerTimeoutMs", wait_charger_timeout_ms);
    progress_report_interval_ms = param_int(test_start, test_end, "progressReportIntervalMs", progress_report_interval_ms);
    if (progress_report_interval_ms <= 0) progress_report_interval_ms = 1000;
    memset(&result, 0, sizeof(result));

    snprintf(data, sizeof(data),
             "{\"phase\":\"ready\",\"waitReadyTimeoutMs\":%d,\"elapsedMs\":%d,"
             "\"externalLoadConflictCheck\":false}",
             wait_ready_timeout_ms, ready_elapsed_ms);
    send_report(fd, "typec_fast_charge", "running", 0,
                "External loads removed, enabling fast charge mode", data);

    snprintf(data, sizeof(data),
             "{\"phase\":\"set_charge_current_limit\",\"chargeCurrentLimitMa\":%d,"
             "\"chargeCurrentLimitCommand\":\"set_500ma\",\"chargeCurrentLimitOk\":false}",
             CHARGE_CURRENT_LIMIT_MA);
    send_report(fd, "typec_fast_charge", "running", 0,
                "Setting charge current limit to 500mA", data);
    if (set_charge_current_limit_500ma() != 0) {
        return send_report(fd, "typec_fast_charge", "failed", 4407,
                           "Unable to set charge current limit to 500mA", data);
    }

    if (set_charge_enabled(1) != 0) {
        snprintf(data, sizeof(data),
                 "{\"chargeControlCommand\":\"enable_charge\",\"chargeControlOk\":false,"
                 "\"chargeCurrentLimitMa\":%d,\"chargeCurrentLimitCommand\":\"set_500ma\",\"chargeCurrentLimitOk\":true,"
                 "\"pmicCommunicationOk\":false,\"chargerConnected\":false,\"charging\":false,"
                 "\"chargeStage\":\"unknown\",\"chargeVoltageMv\":0,\"chargeCurrentMa\":0,\"stable\":false,"
                 "\"stableSamples\":0,\"averageChargeCurrentMa\":0,\"voltageMinMv\":%d,\"voltageMaxMv\":%d,\"currentMinMa\":%d,\"currentMaxMa\":%d}",
                 CHARGE_CURRENT_LIMIT_MA, request.voltage_min_mv, request.voltage_max_mv,
                 request.current_min_ma, request.current_max_ma);
        return send_report(fd, "typec_fast_charge", "failed", 4401,
                           "Unable to enable charge before fast charge test", data);
    }

    /*
     * OTG 通信链路必须常接，PMIC 在部分场景下会把当前 VBUS 提前识别成
     * 类似充电器的输入类型，导致“还没插独立充电器就直接进入检测”。
     * 这里先增加一个固定的人工插线等待窗口：
     * 1. 先提示操作员插入充电线。
     * 2. 等待 15 秒后，再进入后续 PMIC 自动识别逻辑。
     * 这样不会改变后面的真实充电采样和判定流程，只是避免 OTG 误触发。
     */
    elapsed_ms = 0;
    while (elapsed_ms < manual_insert_wait_ms) {
        snprintf(data, sizeof(data),
                 "{\"phase\":\"wait_manual_charger_insert\",\"chargeControlCommand\":\"enable_charge\",\"chargeControlOk\":true,"
                 "\"pmicCommunicationOk\":true,\"chargerConnected\":false,\"charging\":false,\"chargeStage\":\"not_charging\","
                 "\"manualInsertWaitMs\":%d,\"elapsedMs\":%d,\"samplingDurationMs\":%d}",
                 manual_insert_wait_ms, elapsed_ms, request.timeout_ms);
        send_report(fd, "typec_fast_charge", "running", 0,
                    "Please insert charger before automatic detection starts", data);
        sleep_ms_local(progress_report_interval_ms);
        elapsed_ms += progress_report_interval_ms;
    }

    elapsed_ms = 0;
    snprintf(data, sizeof(data),
             "{\"phase\":\"wait_charger\",\"chargeControlCommand\":\"enable_charge\",\"chargeControlOk\":true,"
             "\"pmicCommunicationOk\":true,\"chargerConnected\":false,\"charging\":false,\"chargeStage\":\"not_charging\","
             "\"chargeVoltageMv\":0,\"chargeCurrentMa\":0,\"averageChargeCurrentMa\":0,\"stable\":false,\"stableSamples\":0,"
             "\"waitChargerTimeoutMs\":%d,\"elapsedMs\":0,\"samplingDurationMs\":%d,\"manualInsertWaitMs\":%d}",
             wait_charger_timeout_ms, request.timeout_ms, manual_insert_wait_ms);
    send_report(fd, "typec_fast_charge", "running", 0, "Please insert charger", data);

    /*
     * OTG must stay connected for ADB transport, so VBUS can already be high.
     * Only treat charger insertion as valid when PMIC reports a charger-type
     * VBUS source instead of SDP/CDP/OTG-only power.
     */
    while (elapsed_ms <= wait_charger_timeout_ms) {
        if (read_charge_status_bits(&pmic_status0, &pmic_status1, &vbus_present, &pg_stat, &chg_stat, &vbus_stat, &bc12_done) == 0) {
            if (vbus_present && pg_stat && is_external_charger_type(vbus_stat)) {
                charger_detected = 1;
                last_known_charging = chg_stat != 0;
                last_known_charge_stage = chg_stat;
                last_known_pmic_status0 = pmic_status0;
                last_known_pmic_status1 = pmic_status1;
                last_known_vbus_stat = vbus_stat;
                last_known_bc12_done = bc12_done;
                snprintf(data, sizeof(data),
                         "{\"phase\":\"charger_detected\",\"chargeControlCommand\":\"enable_charge\",\"chargeControlOk\":true,"
                         "\"pmicCommunicationOk\":true,\"chargerConnected\":true,\"charging\":%s,\"chargeStage\":\"%s\","
                         "\"pmicStatus0\":%d,\"pmicStatus1\":%d,\"vbusStat\":%d,\"vbusType\":\"%s\",\"bc12Done\":%d,\"elapsedMs\":%d,\"samplingDurationMs\":%d}",
                         chg_stat != 0 ? "true" : "false",
                         map_charge_stage_name(chg_stat),
                         pmic_status0, pmic_status1, vbus_stat, map_vbus_type_name(vbus_stat), bc12_done, elapsed_ms, request.timeout_ms);
                send_report(fd, "typec_fast_charge", "running", 0, "Charger detected, start sampling", data);
                break;
            }

            snprintf(data, sizeof(data),
                     "{\"phase\":\"wait_charger\",\"chargeControlCommand\":\"enable_charge\",\"chargeControlOk\":true,"
                     "\"pmicCommunicationOk\":true,\"chargerConnected\":%s,\"charging\":false,\"chargeStage\":\"not_charging\","
                     "\"pmicStatus0\":%d,\"pmicStatus1\":%d,\"vbusStat\":%d,\"vbusType\":\"%s\",\"bc12Done\":%d,"
                     "\"waitChargerTimeoutMs\":%d,\"elapsedMs\":%d,\"samplingDurationMs\":%d}",
                     is_external_charger_type(vbus_stat) ? "true" : "false",
                     pmic_status0, pmic_status1, vbus_stat, map_vbus_type_name(vbus_stat), bc12_done,
                     wait_charger_timeout_ms, elapsed_ms, request.timeout_ms);
            send_report(fd, "typec_fast_charge", "running", 0,
                        is_external_charger_type(vbus_stat) ? "Waiting for charger stabilization" : "Waiting for external charger, OTG power does not count",
                        data);
        } else {
            snprintf(data, sizeof(data),
                     "{\"phase\":\"wait_charger\",\"chargeControlCommand\":\"enable_charge\",\"chargeControlOk\":true,"
                     "\"pmicCommunicationOk\":false,\"chargerConnected\":false,\"charging\":false,\"chargeStage\":\"unknown\","
                     "\"vbusType\":\"unknown\",\"waitChargerTimeoutMs\":%d,\"elapsedMs\":%d,\"samplingDurationMs\":%d}",
                     wait_charger_timeout_ms, elapsed_ms, request.timeout_ms);
            send_report(fd, "typec_fast_charge", "running", 0, "Waiting for charger, PMIC status read retrying", data);
        }

        if (elapsed_ms >= wait_charger_timeout_ms) {
            snprintf(data, sizeof(data),
                     "{\"phase\":\"wait_charger\",\"chargeControlCommand\":\"enable_charge\",\"chargeControlOk\":true,"
                     "\"pmicCommunicationOk\":true,\"chargerConnected\":false,\"charging\":false,\"chargeStage\":\"not_charging\","
                     "\"vbusStat\":%d,\"vbusType\":\"%s\",\"bc12Done\":%d,"
                     "\"waitChargerTimeoutMs\":%d,\"elapsedMs\":%d,\"samplingDurationMs\":%d,\"failureReason\":\"charger_insert_timeout\"}",
                     vbus_stat, map_vbus_type_name(vbus_stat), bc12_done,
                     wait_charger_timeout_ms, elapsed_ms, request.timeout_ms);
            return send_report(fd, "typec_fast_charge", "failed", 4405, "Charger insert timeout", data);
        }

        sleep_ms_local(progress_report_interval_ms);
        elapsed_ms += progress_report_interval_ms;
    }

    if (fast_charge_open(&device) != 0 ||
        fast_charge_run_test(&device, &request, &result) != 0) {
        fast_charge_close(&device);
        /*
         * Once fast-charge sampling has actually started, preserve the last PMIC
         * state that successfully proved charger insertion. This lets the host
         * show "charger was detected and charging started, but PMIC reads later
         * failed" instead of incorrectly falling back to "charger not connected".
         */
        if (charger_detected && result.voltage_mv > 0 && result.current_ma > 0) {
            /*
             * 充电采样已经拿到有效电压、电流时，采样后的 PMIC 复读失败只做诊断上报。
             * 最终 PASS/FAIL 仍由上位机依据采样结果判定，3576 不在这里提前拦截。
             */
            snprintf(data, sizeof(data),
                     "{\"phase\":\"ready_for_host_decision\",\"chargeControlCommand\":\"enable_charge\",\"chargeControlOk\":true,"
                     "\"pmicCommunicationOk\":false,\"pmicReadFailedAfterSampling\":true,"
                     "\"chargerConnected\":true,\"charging\":%s,\"chargeStage\":\"%s\","
                     "\"chargeVoltageMv\":%d,\"chargeCurrentMa\":%d,\"rawVoltageSamplesMv\":[%d],\"rawCurrentSamplesMa\":[%d],\"sampleCount\":1,"
                     "\"stable\":%s,\"stableSamples\":%d,"
                     "\"averageChargeCurrentMa\":%d,\"voltageMinMv\":%d,\"voltageMaxMv\":%d,\"currentMinMa\":%d,\"currentMaxMa\":%d,"
                     "\"samplingDurationMs\":%d,"
                     "\"pmicStatus0\":%d,\"pmicStatus1\":%d,\"vbusStat\":%d,\"vbusType\":\"%s\",\"bc12Done\":%d,\"readyForHostDecision\":true}",
                     last_known_charging ? "true" : "false",
                     map_charge_stage_name(last_known_charge_stage),
                     result.voltage_mv, result.current_ma, result.voltage_mv, result.current_ma,
                     result.stable_samples >= request.stable_sample_count ? "true" : "false",
                     result.stable_samples, result.current_ma, request.voltage_min_mv, request.voltage_max_mv,
                     request.current_min_ma, request.current_max_ma, request.timeout_ms,
                     last_known_pmic_status0, last_known_pmic_status1, last_known_vbus_stat,
                     map_vbus_type_name(last_known_vbus_stat), last_known_bc12_done);
            send_report(fd, "typec_fast_charge", "running", 0,
                        "Charging samples captured, waiting for host decision", data);
            switch (wait_test_decision(fd, "typec_fast_charge", request.timeout_ms, &passed)) {
            case 1:
                return send_report(fd, "typec_fast_charge", passed ? "passed" : "failed",
                                   passed ? 0 : 4402,
                                   passed ? "Host confirmed fast charge pass" : "Host confirmed fast charge fail",
                                   data);
            case 0:
                return send_report(fd, "typec_fast_charge", "failed", 4403, "Host decision timed out", data);
            default:
                return send_report(fd, "typec_fast_charge", "failed", 4404, "Unable to read host decision", data);
            }
        }

        snprintf(data, sizeof(data),
                 "{\"phase\":\"sampling_failed\",\"chargeControlCommand\":\"enable_charge\",\"chargeControlOk\":true,"
                 "\"pmicCommunicationOk\":false,\"pmicReadFailedAfterSampling\":true,"
                 "\"chargerConnected\":%s,\"charging\":%s,\"chargeStage\":\"%s\","
                 "\"chargeVoltageMv\":%d,\"chargeCurrentMa\":%d,\"rawVoltageSamplesMv\":[%d],\"rawCurrentSamplesMa\":[%d],\"sampleCount\":1,"
                 "\"stable\":false,\"stableSamples\":0,"
                 "\"averageChargeCurrentMa\":%d,\"voltageMinMv\":%d,\"voltageMaxMv\":%d,\"currentMinMa\":%d,\"currentMaxMa\":%d,"
                 "\"samplingDurationMs\":%d,"
                 "\"pmicStatus0\":%d,\"pmicStatus1\":%d,\"vbusStat\":%d,\"vbusType\":\"%s\",\"bc12Done\":%d}",
                 charger_detected ? "true" : "false",
                 last_known_charging ? "true" : "false",
                 charger_detected ? map_charge_stage_name(last_known_charge_stage) : "unknown",
                 result.voltage_mv, result.current_ma, result.voltage_mv, result.current_ma, result.current_ma,
                 request.voltage_min_mv, request.voltage_max_mv,
                 request.current_min_ma, request.current_max_ma, request.timeout_ms,
                 charger_detected ? last_known_pmic_status0 : pmic_status0,
                 charger_detected ? last_known_pmic_status1 : pmic_status1,
                 charger_detected ? last_known_vbus_stat : vbus_stat,
                 map_vbus_type_name(charger_detected ? last_known_vbus_stat : vbus_stat),
                 charger_detected ? last_known_bc12_done : bc12_done);
        send_report(fd, "typec_fast_charge", "failed",
                    result.error_code == 0 ? 4400 : result.error_code,
                    result.message[0] == '\0' ? "Fast charge test failed" : result.message,
                    data);
        return -1;
    }
    fast_charge_close(&device);
    snprintf(data, sizeof(data),
             "{\"phase\":\"ready_for_host_decision\",\"chargeControlCommand\":\"enable_charge\",\"chargeControlOk\":true,"
             "\"pmicCommunicationOk\":true,\"chargerConnected\":%s,\"charging\":%s,\"chargeStage\":\"%s\","
             "\"chargeVoltageMv\":%d,\"chargeCurrentMa\":%d,\"rawVoltageSamplesMv\":[%d],\"rawCurrentSamplesMa\":[%d],\"sampleCount\":1,"
             "\"stable\":%s,\"stableSamples\":%d,"
             "\"averageChargeCurrentMa\":%d,\"voltageMinMv\":%d,\"voltageMaxMv\":%d,\"currentMinMa\":%d,\"currentMaxMa\":%d,"
             "\"samplingDurationMs\":%d,"
             "\"pmicStatus0\":%d,\"pmicStatus1\":%d,\"vbusStat\":%d,\"vbusType\":\"%s\",\"bc12Done\":%d,\"readyForHostDecision\":true}",
             result.charger_online ? "true" : "false",
             result.charger_online ? "true" : "false",
             result.current_ma >= request.current_min_ma ? "cc" : "attached",
             result.voltage_mv, result.current_ma, result.voltage_mv, result.current_ma,
             result.stable_samples >= request.stable_sample_count ? "true" : "false",
             result.stable_samples, result.current_ma, request.voltage_min_mv, request.voltage_max_mv,
             request.current_min_ma, request.current_max_ma, request.timeout_ms,
             pmic_status0, pmic_status1, vbus_stat, map_vbus_type_name(vbus_stat), bc12_done);
    send_report(fd, "typec_fast_charge", "running", 0, "Waiting for host decision", data);
    switch (wait_test_decision(fd, "typec_fast_charge", request.timeout_ms, &passed)) {
    case 1:
        return send_report(fd, "typec_fast_charge", passed ? "passed" : "failed",
                           passed ? 0 : 4402,
                           passed ? "Host confirmed fast charge pass" : "Host confirmed fast charge fail",
                           data);
    case 0:
        return send_report(fd, "typec_fast_charge", "failed", 4403, "Host decision timed out", data);
    default:
        return send_report(fd, "typec_fast_charge", "failed", 4404, "Unable to read host decision", data);
    }
}

static int run_battery_management(int fd, const char *test_start, const char *test_end)
{
    char data[2048];
    char status[64];
    char required_status[32] = "Discharging";
    char status_path[256] = "/sys/class/power_supply/bq2579x-charger/status";
    char current_path[256] = "/sys/class/power_supply/cw221X-bat/current_now";
    char voltage_path[256] = "/sys/class/power_supply/cw221X-bat/voltage_now";
    int confirmation_timeout_ms = 15000;
    int sampling_duration_ms = 4000;
    int sample_interval_ms = 500;
    int minimum_valid_samples = 6;
    int voltage_min_mv = 7600;
    int voltage_max_mv = 8400;
    int current_min_ma = 100;
    int current_max_ma = 500;
    int stability_tolerance_ma = 80;
    int confirmed = 0;
    int sample_count = 0;
    int valid_samples = 0;
    long long voltage_sum_mv = 0;
    long long current_sum_ma = 0;
    int voltage_min_seen = 0;
    int voltage_max_seen = 0;
    int current_min_seen = 0;
    int current_max_seen = 0;
    int elapsed = 0;

    param_string(test_start, test_end, "chargerStatusPath", status_path, sizeof(status_path));
    param_string(test_start, test_end, "currentPath", current_path, sizeof(current_path));
    param_string(test_start, test_end, "voltagePath", voltage_path, sizeof(voltage_path));
    param_string(test_start, test_end, "requiredStatus", required_status, sizeof(required_status));
    /* Sampling duration, interval, sample count and confirmation timeout are fixed by the test procedure. */
    voltage_min_mv = param_int(test_start, test_end, "voltageMinMv", voltage_min_mv);
    voltage_max_mv = param_int(test_start, test_end, "voltageMaxMv", voltage_max_mv);
    current_min_ma = param_int(test_start, test_end, "dischargeCurrentMinMa", current_min_ma);
    current_max_ma = param_int(test_start, test_end, "dischargeCurrentMaxMa", current_max_ma);
    stability_tolerance_ma = param_int(test_start, test_end, "currentStabilityToleranceMa", stability_tolerance_ma);
    if (sample_interval_ms < 100) sample_interval_ms = 100;
    if (sampling_duration_ms < sample_interval_ms) sampling_duration_ms = sample_interval_ms;

    if (read_sysfs_text(status_path, status, sizeof(status)) != 0) {
        return send_report(fd, "battery_management", "failed", 4701, "Unable to read charger status", "{\"failureReason\":\"charger_status_read_failed\"}");
    }

    if (strcmp(status, required_status) != 0) {
        snprintf(data, sizeof(data),
                 "{\"phase\":\"wait_unplug_charger\",\"chargerStatus\":\"%s\",\"requiredStatus\":\"%s\","
                 "\"requiresOperatorConfirmation\":true,\"operatorConfirmationTimeoutMs\":%d}",
                 status, required_status, confirmation_timeout_ms);
        send_report(fd, "battery_management", "running", 0, "Unplug charger and external devices, then confirm", data);
        switch (wait_test_decision(fd, "battery_management", confirmation_timeout_ms, &confirmed)) {
        case 1: break;
        case 0: return send_report(fd, "battery_management", "failed", 4708, "Operator confirmation timed out", data);
        default: return send_report(fd, "battery_management", "failed", 4708, "Unable to read operator confirmation", data);
        }
        if (!confirmed || read_sysfs_text(status_path, status, sizeof(status)) != 0 || strcmp(status, required_status) != 0) {
            snprintf(data, sizeof(data),
                     "{\"phase\":\"status_recheck_failed\",\"chargerStatus\":\"%s\",\"requiredStatus\":\"%s\","
                     "\"failureReason\":\"not_discharging_after_confirmation\"}", status, required_status);
            return send_report(fd, "battery_management", "failed", 4702, "Device is still not discharging after confirmation", data);
        }
    }

    while (elapsed < sampling_duration_ms) {
        long long current_ua;
        long long voltage_uv;
        int current_ma;
        int voltage_mv;
        ++sample_count;
        if (read_sysfs_text(status_path, status, sizeof(status)) == 0 &&
            strcmp(status, required_status) == 0 &&
            read_sysfs_long(current_path, &current_ua) == 0 &&
            read_sysfs_long(voltage_path, &voltage_uv) == 0 &&
            current_ua < 0) {
            current_ma = (int)((-current_ua + 500) / 1000);
            voltage_mv = (int)((voltage_uv + 500) / 1000);
            if (valid_samples == 0) {
                voltage_min_seen = voltage_max_seen = voltage_mv;
                current_min_seen = current_max_seen = current_ma;
            } else {
                if (voltage_mv < voltage_min_seen) voltage_min_seen = voltage_mv;
                if (voltage_mv > voltage_max_seen) voltage_max_seen = voltage_mv;
                if (current_ma < current_min_seen) current_min_seen = current_ma;
                if (current_ma > current_max_seen) current_max_seen = current_ma;
            }
            voltage_sum_mv += voltage_mv;
            current_sum_ma += current_ma;
            ++valid_samples;
            snprintf(data, sizeof(data),
                     "{\"phase\":\"sampling\",\"chargerStatus\":\"%s\",\"elapsedMs\":%d,"
                     "\"samplingDurationMs\":%d,\"sampleCount\":%d,\"validSampleCount\":%d,"
                     "\"voltageMv\":%d,\"dischargeCurrentMa\":%d}",
                     status, elapsed, sampling_duration_ms, sample_count, valid_samples, voltage_mv, current_ma);
            send_report(fd, "battery_management", "running", 0, "Battery discharge sampling", data);
        }
        sleep_ms_local(sample_interval_ms);
        elapsed += sample_interval_ms;
    }

    if (valid_samples < minimum_valid_samples) {
        snprintf(data, sizeof(data), "{\"phase\":\"completed\",\"sampleCount\":%d,\"validSampleCount\":%d,\"minimumValidSamples\":%d,\"failureReason\":\"insufficient_valid_samples\"}", sample_count, valid_samples, minimum_valid_samples);
        return send_report(fd, "battery_management", "failed", 4705, "Insufficient valid discharge samples", data);
    }

    {
        int avg_voltage_mv = (int)(voltage_sum_mv / valid_samples);
        int avg_current_ma = (int)(current_sum_ma / valid_samples);
        int current_ripple_ma = current_max_seen - current_min_seen;
        int passed = avg_voltage_mv >= voltage_min_mv && avg_voltage_mv <= voltage_max_mv &&
                     avg_current_ma >= current_min_ma && avg_current_ma <= current_max_ma;
        int code = passed ? 0 : avg_voltage_mv < voltage_min_mv || avg_voltage_mv > voltage_max_mv ? 4704 :
                   avg_current_ma < current_min_ma || avg_current_ma > current_max_ma ? 4703 : 4705;
        snprintf(data, sizeof(data),
                 "{\"phase\":\"completed\",\"chargerStatus\":\"%s\",\"sampleCount\":%d,\"validSampleCount\":%d,"
                 "\"averageVoltageMv\":%d,\"minimumVoltageMv\":%d,\"maximumVoltageMv\":%d,"
                 "\"averageDischargeCurrentMa\":%d,\"minimumDischargeCurrentMa\":%d,\"maximumDischargeCurrentMa\":%d,"
                 "\"currentRippleMa\":%d,\"voltageMinMv\":%d,\"voltageMaxMv\":%d,"
                 "\"dischargeCurrentMinMa\":%d,\"dischargeCurrentMaxMa\":%d,\"currentStabilityToleranceMa\":%d}",
                 status, sample_count, valid_samples, avg_voltage_mv, voltage_min_seen, voltage_max_seen,
                 avg_current_ma, current_min_seen, current_max_seen, current_ripple_ma, voltage_min_mv, voltage_max_mv,
                 current_min_ma, current_max_ma, stability_tolerance_ma);
        return send_report(fd, "battery_management", passed ? "passed" : "failed", code,
                           passed ? "Battery discharge test passed" : "Battery discharge measurement out of range", data);
    }
}

static int elapsed_ms(const struct timespec *start, const struct timespec *now)
{
    return (int)((now->tv_sec - start->tv_sec) * 1000 +
                 (now->tv_nsec - start->tv_nsec) / 1000000);
}

static void format_timestamp_now(char *buffer, size_t buffer_size)
{
    time_t now;
    struct tm tm_value;
    if (buffer == NULL || buffer_size == 0) return;
    now = time(NULL);
    localtime_r(&now, &tm_value);
    strftime(buffer, buffer_size, "%Y-%m-%dT%H:%M:%S%z", &tm_value);
}

static int wait_operator_decision(int fd, const char *test_id, int timeout_ms, int *passed)
{
    struct timespec start, now;
    char line[PROTOCOL_MAX_LINE];

    if (passed == NULL) return -1;
    *passed = 0;
    clock_gettime(CLOCK_MONOTONIC, &start);
    for (;;) {
        int remaining;
        fd_set read_fds;
        struct timeval tv;
        int ready;

        clock_gettime(CLOCK_MONOTONIC, &now);
        remaining = timeout_ms - elapsed_ms(&start, &now);
        if (remaining <= 0) return 0;

        FD_ZERO(&read_fds);
        FD_SET(fd, &read_fds);
        tv.tv_sec = remaining / 1000;
        tv.tv_usec = (remaining % 1000) * 1000;
        ready = select(fd + 1, &read_fds, NULL, NULL, &tv);
        if (ready < 0) return -1;
        if (ready == 0) return 0;
        if (protocol_read_line(fd, line, sizeof(line)) <= 0) return -1;
        if (strstr(line, "\"event\":\"operator.decision\"") == NULL) continue;
        if (strstr(line, test_id) == NULL) continue;
        *passed = strstr(line, "\"passed\":true") != NULL;
        return 1;
    }
}

static int wait_test_decision(int fd, const char *test_id, int timeout_ms, int *passed)
{
    struct timespec start, now;
    char line[PROTOCOL_MAX_LINE];

    if (passed == NULL) return -1;
    *passed = 0;
    clock_gettime(CLOCK_MONOTONIC, &start);
    for (;;) {
        int remaining;
        fd_set read_fds;
        struct timeval tv;
        int ready;

        clock_gettime(CLOCK_MONOTONIC, &now);
        remaining = timeout_ms - elapsed_ms(&start, &now);
        if (remaining <= 0) return 0;

        FD_ZERO(&read_fds);
        FD_SET(fd, &read_fds);
        tv.tv_sec = remaining / 1000;
        tv.tv_usec = (remaining % 1000) * 1000;
        ready = select(fd + 1, &read_fds, NULL, NULL, &tv);
        if (ready < 0) return -1;
        if (ready == 0) return 0;
        if (protocol_read_line(fd, line, sizeof(line)) <= 0) return -1;
        if (strstr(line, "\"event\":\"test.decision\"") == NULL) continue;
        if (strstr(line, test_id) == NULL) continue;
        *passed = strstr(line, "\"passed\":true") != NULL;
        return 1;
    }
}

static int run_manual_observation(int fd, const char *test_id, const char *display_name,
                                  const char *test_start, const char *test_end)
{
    int passed = 0;
    int timeout_ms = param_int(test_start, test_end, "timeoutMs", 60000);
    char data[512];

    if (timeout_ms < 30000) timeout_ms = 30000;
    snprintf(data, sizeof(data),
             "{\"manualObserved\":true,\"requiresOperatorDecision\":true,\"timeoutMs\":%d,\"expectedAction\":\"Observe %s output and confirm pass or fail\"}",
             timeout_ms, display_name);
    fprintf(stderr, "[HDMI] waiting decision fd=%d test=%s timeoutMs=%d\n", fd, test_id, timeout_ms);
    send_report(fd, test_id, "running", 0, "Waiting for operator decision", data);

    switch (wait_operator_decision(fd, test_id, timeout_ms, &passed)) {
    case 1:
        fprintf(stderr, "[HDMI] decision received fd=%d test=%s passed=%s\n", fd, test_id, passed ? "true" : "false");
        snprintf(data, sizeof(data),
                 "{\"manualObserved\":true,\"operatorConfirmed\":%s}",
                 passed ? "true" : "false");
        return send_report(fd, test_id, passed ? "passed" : "failed",
                           passed ? 0 : 3910,
                           passed ? "Operator confirmed pass" : "Operator confirmed fail",
                           data) == 0 && passed ? 0 : -1;
    case 0:
        fprintf(stderr, "[HDMI] decision timeout fd=%d test=%s timeoutMs=%d\n", fd, test_id, timeout_ms);
        snprintf(data, sizeof(data),
                 "{\"manualObserved\":true,\"operatorConfirmed\":false,\"timeoutMs\":%d}",
                 timeout_ms);
        send_report(fd, test_id, "failed", 3911, "Operator decision timed out", data);
        return -1;
    default:
        fprintf(stderr, "[HDMI] decision read failed fd=%d test=%s errno=%d\n", fd, test_id, errno);
        send_report(fd, test_id, "failed", 3912, "Unable to read operator decision", "{}");
        return -1;
    }
}

static int wait_operator_decision_or_disconnect(int fd, const char *test_id, int timeout_ms, int *passed, int *disconnected)
{
    struct timespec start, now;
    char line[PROTOCOL_MAX_LINE];

    if (passed == NULL || disconnected == NULL) return -1;
    *passed = 0;
    *disconnected = 0;
    clock_gettime(CLOCK_MONOTONIC, &start);
    for (;;) {
        int remaining;
        fd_set read_fds;
        struct timeval tv;
        int ready;

        clock_gettime(CLOCK_MONOTONIC, &now);
        remaining = timeout_ms - elapsed_ms(&start, &now);
        if (remaining <= 0) return 0;

        FD_ZERO(&read_fds);
        FD_SET(fd, &read_fds);
        tv.tv_sec = remaining / 1000;
        tv.tv_usec = (remaining % 1000) * 1000;
        ready = select(fd + 1, &read_fds, NULL, NULL, &tv);
        if (ready < 0) return -1;
        if (ready == 0) return 0;
        if (protocol_read_line(fd, line, sizeof(line)) <= 0) {
            *disconnected = 1;
            return 2;
        }
        if (strstr(line, "\"event\":\"operator.decision\"") == NULL) continue;
        if (strstr(line, test_id) == NULL) continue;
        *passed = strstr(line, "\"passed\":true") != NULL;
        return 1;
    }
}

static int write_fan_pwm(const char *path, int value)
{
    char text[32];
    int length;
    int fd;
    if (path == NULL || path[0] == '\0') return -1;
    fd = open(path, O_WRONLY | O_CLOEXEC);
    if (fd < 0) return -1;
    length = snprintf(text, sizeof(text), "%d\n", value);
    if (write(fd, text, (size_t)length) != length) {
        close(fd);
        return -1;
    }
    close(fd);
    return 0;
}

static int read_fan_tach(const char *path, int *value)
{
    FILE *file = fopen(path, "r");
    int scanned;
    if (file == NULL) return -1;
    scanned = fscanf(file, "%d", value);
    fclose(file);
    return scanned == 1 ? 0 : -1;
}

static int read_fan_tach_stable(const char *path, int sample_count, int interval_ms,
                                int *last_value, int *running_seen, int *samples_read)
{
    int index;
    int value = 0;
    if (last_value == NULL || running_seen == NULL || samples_read == NULL) return -1;
    *last_value = 0;
    *running_seen = 0;
    *samples_read = 0;
    if (sample_count <= 0) sample_count = 1;
    if (interval_ms < 0) interval_ms = 0;
    for (index = 0; index < sample_count; ++index) {
        if (read_fan_tach(path, &value) != 0) return -1;
        *last_value = value;
        *samples_read = index + 1;
        if (value == 1) {
            *running_seen = 1;
            return 0;
        }
        if (index + 1 < sample_count) sleep_ms_local(interval_ms);
    }
    return 0;
}

static int run_finished_product_fan(int fd, const char *test_start, const char *test_end)
{
    char pwm_path[192] = "/sys/class/hwmon/hwmon12/pwm1";
    char tach_path[192] = "/sys/class/hwmon/hwmon12/tach_rpm";
    char data[1024];
    int start_value = param_int(test_start, test_end, "startValue", 100);
    int stop_value = param_int(test_start, test_end, "stopValue", 0);
    int settle_ms = param_int(test_start, test_end, "tachSettleMs", 1000);
    int tach_sample_count = param_int(test_start, test_end, "tachSampleCount", 3);
    int tach_sample_interval_ms = param_int(test_start, test_end, "tachSampleIntervalMs", 300);
    int tach_value = 0;
    int tach_running_seen = 0;
    int tach_samples_read = 0;

    param_string(test_start, test_end, "pwmPath", pwm_path, sizeof(pwm_path));
    param_string(test_start, test_end, "tachPath", tach_path, sizeof(tach_path));
    if (settle_ms < 0) settle_ms = 0;
    if (write_fan_pwm(pwm_path, start_value) != 0) {
        send_report(fd, "fan", "failed", 3920, "Unable to start fan PWM", "{}");
        return -1;
    }
    snprintf(data, sizeof(data),
             "{\"automatic\":true,\"pwmPath\":\"%s\",\"tachPath\":\"%s\",\"startValue\":%d,\"stopValue\":%d,\"tachSettleMs\":%d}",
             pwm_path, tach_path, start_value, stop_value, settle_ms);
    send_report(fd, "fan", "running", 0, "Fan started; checking tach_rpm automatically", data);
    if (settle_ms > 0) {
        struct timespec settle_time = {
            .tv_sec = settle_ms / 1000,
            .tv_nsec = (long)(settle_ms % 1000) * 1000000L
        };
        nanosleep(&settle_time, NULL);
    }
    if (read_fan_tach_stable(tach_path, tach_sample_count, tach_sample_interval_ms,
                             &tach_value, &tach_running_seen, &tach_samples_read) != 0) {
        write_fan_pwm(pwm_path, stop_value);
        snprintf(data, sizeof(data), "{\"automatic\":true,\"tachPath\":\"%s\",\"tachRead\":false}", tach_path);
        send_report(fd, "fan", "failed", 3922, "Unable to read fan tach_rpm", data);
        return -1;
    }
    if (write_fan_pwm(pwm_path, stop_value) != 0) {
        snprintf(data, sizeof(data), "{\"automatic\":true,\"tachRpm\":%d,\"pwmStopped\":false}", tach_value);
        send_report(fd, "fan", "failed", 3921, "Unable to stop fan PWM", data);
        return -1;
    }
    snprintf(data, sizeof(data),
             "{\"automatic\":true,\"tachPath\":\"%s\",\"tachRpm\":%d,\"fanRunning\":%s,"
             "\"tachSampleCount\":%d,\"tachSamplesRead\":%d,\"tachSampleIntervalMs\":%d,\"pwmStopped\":true}",
             tach_path, tach_value, tach_running_seen ? "true" : "false",
             tach_sample_count, tach_samples_read, tach_sample_interval_ms);
    return send_report(fd, "fan", tach_running_seen ? "passed" : "failed", tach_running_seen ? 0 : 3910,
                       tach_running_seen ? "Fan tach_rpm indicates running" : "Fan tach_rpm indicates stopped", data) == 0 && tach_running_seen ? 0 : -1;
}

static int run_ethernet_led_command(const char *interface_name, int gigabit)
{
    char command[256];
    const char *speed_args = gigabit
        ? "autoneg on advertise 0x0028"
        : "speed 100 duplex full autoneg off";
    if (geteuid() == 0) {
        snprintf(command, sizeof(command), "ethtool -s %s %s >/dev/null 2>&1", interface_name, speed_args);
    } else {
        snprintf(command, sizeof(command), "sudo -n ethtool -s %s %s >/dev/null 2>&1", interface_name, speed_args);
    }
    return system(command) == 0 ? 0 : -1;
}

static int run_ethernet_led_shell(const char *command)
{
    int rc;
    if (command == NULL || command[0] == '\0') return -1;
    rc = system(command);
    return rc == 0 ? 0 : -1;
}

static void reconnect_ethernet_led_interface(const char *interface_name)
{
    char command[256];
    snprintf(command, sizeof(command), "ip link set dev %s up >/dev/null 2>&1", interface_name);
    (void)run_ethernet_led_shell(command);
    snprintf(command, sizeof(command), "nmcli device reapply %s >/dev/null 2>&1", interface_name);
    (void)run_ethernet_led_shell(command);
    snprintf(command, sizeof(command), "nmcli device connect %s >/dev/null 2>&1", interface_name);
    (void)run_ethernet_led_shell(command);
}

static int read_ethernet_led_speed_mbps(const char *interface_name)
{
    char path[160];
    FILE *file;
    int speed_mbps = 0;
    if (interface_name == NULL || interface_name[0] == '\0') return -1;
    snprintf(path, sizeof(path), "/sys/class/net/%s/speed", interface_name);
    file = fopen(path, "r");
    if (file == NULL) return -1;
    if (fscanf(file, "%d", &speed_mbps) != 1) {
        fclose(file);
        return -1;
    }
    fclose(file);
    return speed_mbps;
}

static int wait_ethernet_led_speed(const char *interface_name, int expected_speed_mbps,
                                   int timeout_ms, int *actual_speed_mbps)
{
    int elapsed_ms = 0;
    int speed_mbps = -1;
    if (timeout_ms <= 0) timeout_ms = 10000;
    while (elapsed_ms <= timeout_ms) {
        if (net_carrier_is_up(interface_name)) {
            speed_mbps = read_ethernet_led_speed_mbps(interface_name);
            if (speed_mbps == expected_speed_mbps) {
                if (actual_speed_mbps != NULL) *actual_speed_mbps = speed_mbps;
                return 0;
            }
        }
        sleep_ms_local(200);
        elapsed_ms += 200;
    }
    if (actual_speed_mbps != NULL) *actual_speed_mbps = speed_mbps;
    return -1;
}

static void restore_ethernet_led_autoneg(const char *interface_name)
{
    char command[256];
    if (geteuid() == 0) {
        snprintf(command, sizeof(command), "ethtool -s %s autoneg on advertise 0x0028 >/dev/null 2>&1", interface_name);
    } else {
        snprintf(command, sizeof(command), "sudo -n ethtool -s %s autoneg on advertise 0x0028 >/dev/null 2>&1", interface_name);
    }
    (void)run_ethernet_led_shell(command);

    reconnect_ethernet_led_interface(interface_name);
}

static int run_ethernet_led(int fd, const char *test_start, const char *test_end)
{
    char interface_name[64] = "end0";
    char data[768];
    char led_100m[32] = "green";
    char led_1000m[32] = "yellow";
    int wait_cable_timeout_ms = param_int(test_start, test_end, "waitCableTimeoutMs", 15000);
    int progress_report_interval_ms = param_int(test_start, test_end, "progressReportIntervalMs", 1000);
    int phase_ms = param_int(test_start, test_end, "phaseDurationMs", 2000);
    int settle_ms = param_int(test_start, test_end, "settleMs", 2000);
    int speed_wait_timeout_ms = param_int(test_start, test_end, "speedWaitTimeoutMs", 10000);
    int cycle_count = param_int(test_start, test_end, "cycleCount", 1);
    int timeout_ms = param_int(test_start, test_end, "manualDecisionTimeoutMs", 15000);
    int reconnect_delay_ms = param_int(test_start, test_end, "reconnectDelayMs", 25000);
    int pre_disconnect_delay_ms = param_int(test_start, test_end, "preDisconnectDelayMs", 1000);
    int resume_after_reconnect = param_bool(test_start, test_end, "resumeAfterReconnect", 0);
    int wait_cable_elapsed_ms = 0;
    int disconnected = 0;
    int passed = 0;
    int resumed_after_reconnect = 0;
    int cycle;
    int phase;

    param_string(test_start, test_end, "interfaceName", interface_name, sizeof(interface_name));
    param_string(test_start, test_end, "led100mColor", led_100m, sizeof(led_100m));
    param_string(test_start, test_end, "led1000mColor", led_1000m, sizeof(led_1000m));
    timeout_ms = param_int(test_start, test_end, "timeoutMs", timeout_ms);
    if (wait_cable_timeout_ms <= 0) wait_cable_timeout_ms = 15000;
    if (progress_report_interval_ms <= 0) progress_report_interval_ms = 1000;
    if (phase_ms <= 0) phase_ms = 2000;
    if (settle_ms < 0) settle_ms = 0;
    if (speed_wait_timeout_ms <= 0) speed_wait_timeout_ms = 10000;
    if (cycle_count <= 0) cycle_count = 1;
    if (cycle_count > 10) cycle_count = 10;
    if (timeout_ms <= 0) timeout_ms = 15000;
    if (reconnect_delay_ms < 1000) reconnect_delay_ms = 25000;
    if (pre_disconnect_delay_ms < 0) pre_disconnect_delay_ms = 0;
    ethernet_led_log("start", resume_after_reconnect ? "resumeAfterReconnect=true" : "resumeAfterReconnect=false");

    if (resume_after_reconnect) {
        ethernet_led_log("resume_check", ethernet_led_resume_pending ? "checkpoint_pending=true" : "checkpoint_pending=false");
        if (!ethernet_led_resume_pending) {
            ethernet_led_log("fail", "missing_reconnect_checkpoint code=4814");
            return send_report(fd, "ethernet_led", "failed", 4814,
                               "No pending Ethernet LED reconnect checkpoint", "{}");
        }
        if (ethernet_led_resume_code != 0) {
            ethernet_led_log("fail_during_disconnect", "resume_code_nonzero");
            int pending_code = ethernet_led_resume_code;
            ethernet_led_resume_pending = 0;
            ethernet_led_resume_code = 0;
            return send_report(fd, "ethernet_led", "failed", pending_code,
                               pending_code == 4812 ? "Unable to switch Ethernet LED speed mode" :
                                                      "Ethernet LED speed mode did not become stable",
                               "{\"phase\":\"failed_during_disconnect\"}");
        }
        resumed_after_reconnect = 1;
        /* The control connection may have dropped during the first LED phase.
         * Resume the complete LED sequence after reconnecting instead of
         * jumping straight to operator confirmation; otherwise PASS could be
         * shown after only the green/100M phase. The existing reconnect flow
         * remains unchanged and the sequence will establish a fresh checkpoint
         * before entering the decision phase. */
        ethernet_led_log("resume_wait", "waiting_for_original_sequence");
        {
            int wait_ms = 0;
            while (!ethernet_led_sequence_complete && ethernet_led_resume_code == 0 && wait_ms < 30000) {
                sleep_ms_local(250);
                wait_ms += 250;
            }
        }
        if (ethernet_led_resume_code != 0) {
            ethernet_led_log("fail_during_disconnect", "original_sequence_failed");
            return send_report(fd, "ethernet_led", "failed", ethernet_led_resume_code,
                               "Ethernet LED sequence failed during disconnect", "{\"phase\":\"failed_during_disconnect\"}");
        }
        if (!ethernet_led_sequence_complete) {
            ethernet_led_log("fail", "original_sequence_wait_timeout code=4813");
            return send_report(fd, "ethernet_led", "failed", 4813,
                               "Ethernet LED sequence did not complete after reconnect", "{\"phase\":\"resume_wait_timeout\"}");
        }
        goto wait_for_decision;
    }

    snprintf(data, sizeof(data),
             "{\"interfaceName\":\"%s\",\"phase\":\"wait_cable\",\"ethernetLinkUp\":false,"
             "\"requiresCableInsert\":true,\"waitCableTimeoutMs\":%d,\"elapsedMs\":0}",
             interface_name, wait_cable_timeout_ms);
    send_report(fd, "ethernet_led", "running", 0, "Insert Ethernet cable for LED test", data);
    ethernet_led_log("wait_cable", "waiting_for_carrier");

    while (wait_cable_elapsed_ms < wait_cable_timeout_ms && !net_carrier_is_up(interface_name)) {
        sleep_ms_local(progress_report_interval_ms);
        wait_cable_elapsed_ms += progress_report_interval_ms;
        snprintf(data, sizeof(data),
                 "{\"interfaceName\":\"%s\",\"phase\":\"wait_cable\",\"ethernetLinkUp\":false,"
                 "\"requiresCableInsert\":true,\"waitCableTimeoutMs\":%d,\"elapsedMs\":%d}",
                 interface_name, wait_cable_timeout_ms, wait_cable_elapsed_ms);
        send_report(fd, "ethernet_led", "running", 0, "Waiting for Ethernet cable for LED test", data);
    }

    if (!net_carrier_is_up(interface_name)) {
        ethernet_led_log("fail", "ethernet_insert_timeout code=4811");
        snprintf(data, sizeof(data),
                 "{\"interfaceName\":\"%s\",\"phase\":\"wait_cable\",\"ethernetLinkUp\":false,"
                 "\"requiresCableInsert\":true,\"waitCableTimeoutMs\":%d,\"failureReason\":\"ethernet_insert_timeout\"}",
                 interface_name, wait_cable_timeout_ms);
        send_report(fd, "ethernet_led", "failed", 4811, "Ethernet cable insert timeout before LED test", data);
        return -1;
    }

    snprintf(data, sizeof(data),
             "{\"interfaceName\":\"%s\",\"phase\":\"prepare_disconnect\",\"reconnectRequired\":true,"
             "\"resumeAfterReconnect\":true,\"reconnectDelayMs\":%d,\"cycleCount\":%d,"
             "\"led100mColor\":\"%s\",\"led1000mColor\":\"%s\","
             "\"phaseDurationMs\":%d,\"settleMs\":%d,\"preDisconnectDelayMs\":%d}",
             interface_name, reconnect_delay_ms, cycle_count, led_100m, led_1000m,
             phase_ms, settle_ms, pre_disconnect_delay_ms);
    if (send_report(fd, "ethernet_led", "running", 0,
                    "Ethernet LED switching will temporarily disconnect the control link", data) != 0) {
        return -2;
    }
    if (pre_disconnect_delay_ms > 0) sleep_ms_local(pre_disconnect_delay_ms);
    ethernet_led_log("prepare_disconnect", "saving_checkpoint_and_shutdown");
    ethernet_led_resume_pending = 1;
    ethernet_led_resume_code = 0;
    ethernet_led_sequence_complete = 0;
    (void)shutdown(fd, SHUT_RDWR);

    for (cycle = 1; cycle <= cycle_count; ++cycle) {
        for (phase = 0; phase < 2; ++phase) {
            const int gigabit = phase == 1;
            const int expected_speed_mbps = gigabit ? 1000 : 100;
            const char *phase_name = gigabit ? "show_1000m" : "show_100m";
            const char *expected_led = gigabit ? led_1000m : led_100m;
            int actual_speed_mbps = -1;
            ethernet_led_log(phase_name, gigabit ? "begin expected=1000Mbps led=yellow" : "begin expected=100Mbps led=green");

            if (run_ethernet_led_command(interface_name, gigabit) != 0) {
                ethernet_led_log("fail", "ethtool_command_failed code=4812");
                restore_ethernet_led_autoneg(interface_name);
                snprintf(data, sizeof(data),
                         "{\"interfaceName\":\"%s\",\"phase\":\"%s\",\"cycleIndex\":%d,\"cycleCount\":%d,"
                         "\"expectedLed\":\"%s\",\"expectedSpeedMbps\":%d,\"failureReason\":\"ethtool_command_failed\"}",
                         interface_name, phase_name, cycle, cycle_count, expected_led, expected_speed_mbps);
                send_report(fd, "ethernet_led", "failed", 4812, "Unable to switch Ethernet LED speed mode", data);
                ethernet_led_resume_code = 4812;
                return -2;
            }

            if (settle_ms > 0) sleep_ms_local(settle_ms);
            reconnect_ethernet_led_interface(interface_name);
            ethernet_led_log("link_reconnect", phase_name);
            if (wait_ethernet_led_speed(interface_name, expected_speed_mbps,
                                        speed_wait_timeout_ms, &actual_speed_mbps) != 0) {
                ethernet_led_log("fail", gigabit ? "yellow_speed_verify_failed code=4813" : "green_speed_verify_failed code=4813");
                restore_ethernet_led_autoneg(interface_name);
                snprintf(data, sizeof(data),
                         "{\"interfaceName\":\"%s\",\"phase\":\"%s\",\"cycleIndex\":%d,\"cycleCount\":%d,"
                         "\"expectedLed\":\"%s\",\"expectedSpeedMbps\":%d,\"actualSpeedMbps\":%d,"
                         "\"failureReason\":\"speed_verify_failed\"}",
                         interface_name, phase_name, cycle, cycle_count, expected_led,
                         expected_speed_mbps, actual_speed_mbps);
                send_report(fd, "ethernet_led", "failed", 4813, "Ethernet LED speed mode did not become stable", data);
                ethernet_led_resume_code = 4813;
                return -2;
            }

            snprintf(data, sizeof(data),
                     "{\"interfaceName\":\"%s\",\"phase\":\"%s\",\"ethernetLinkUp\":true,"
                     "\"cycleIndex\":%d,\"cycleCount\":%d,\"expectedLed\":\"%s\","
                     "\"expectedSpeedMbps\":%d,\"actualSpeedMbps\":%d,"
                     "\"phaseDurationMs\":%d,\"settleMs\":%d,\"timeoutMs\":%d}",
                     interface_name, phase_name, cycle, cycle_count, expected_led,
                     expected_speed_mbps, actual_speed_mbps, phase_ms, settle_ms, timeout_ms);
            send_report(fd, "ethernet_led", "running", 0,
                        gigabit ? "Ethernet LED 1000M mode; observe yellow LED" :
                                  "Ethernet LED 100M mode; observe green LED",
                        data);
            ethernet_led_log("observe", phase_name);
            sleep_ms_local(phase_ms);
            ethernet_led_log("observe_done", phase_name);
        }
    }

    restore_ethernet_led_autoneg(interface_name);
    ethernet_led_log("sequence_done", resumed_after_reconnect ? "resumed_connection=true" : "initial_disconnect_complete");
    ethernet_led_sequence_complete = 1;
    if (resumed_after_reconnect) goto wait_for_decision;
    return -2;

wait_for_decision:
    ethernet_led_log("operator_confirm_sequence", "PASS_FAIL_now_allowed");
    snprintf(data, sizeof(data),
             "{\"phase\":\"operator_confirm_sequence\",\"led100mColor\":\"%s\","
             "\"led1000mColor\":\"%s\",\"requiresOperatorDecision\":true,\"timeoutMs\":%d}",
             led_100m, led_1000m, timeout_ms);
    send_report(fd, "ethernet_led", "running", 0, "Confirm the Ethernet LED sequence", data);
    switch (wait_operator_decision_or_disconnect(fd, "ethernet_led", timeout_ms, &passed, &disconnected)) {
    case 1:
        goto ethernet_led_decision_done;
    case 0:
        ethernet_led_log("fail", "operator_decision_timeout code=3911");
        restore_ethernet_led_autoneg(interface_name);
        ethernet_led_resume_pending = 0;
        ethernet_led_resume_code = 0;
        send_report(fd, "ethernet_led", "failed", 3911, "Operator decision timed out", "{}");
        return -1;
    case 2:
        restore_ethernet_led_autoneg(interface_name);
        return -2;
    default:
        passed = 0;
        goto ethernet_led_decision_done;
    }

ethernet_led_decision_done:
        ethernet_led_log("decision", passed ? "PASS" : "FAIL");
        restore_ethernet_led_autoneg(interface_name);
        snprintf(data, sizeof(data),
                 "{\"manualObserved\":true,\"operatorConfirmed\":%s,\"interfaceName\":\"%s\","
                 "\"displayMode\":\"100m_1000m_led_sequence\",\"timeoutMs\":%d}",
                 passed ? "true" : "false", interface_name, timeout_ms);
        ethernet_led_resume_pending = 0;
        ethernet_led_resume_code = 0;
        return send_report(fd, "ethernet_led", passed ? "passed" : "failed",
                           passed ? 0 : 3910,
                           passed ? "Operator confirmed Ethernet LEDs pass" :
                                    "Operator confirmed Ethernet LEDs fail",
                           data) == 0 && passed ? 0 : -1;
}

static int run_finished_product_indicator_led(int fd, const char *test_start, const char *test_end)
{
#define INDICATOR_LED_ON_BRIGHTNESS 255
#define INDICATOR_LED_PHASE_COUNT 3
    struct indicator_led_device device;
    struct indicator_led_result result;
    int timeout_ms = param_int(test_start, test_end, "timeoutMs", 60000);
    int phase_ms = param_int(test_start, test_end, "phaseDurationMs", 2000);
    int red_green_overlap_ms = param_int(test_start, test_end, "redGreenOverlapMs", 200);
    int i2c_timeout_ms = param_int(test_start, test_end, "i2cTimeoutMs", 3000);
    int retry_interval_ms = param_int(test_start, test_end, "i2cRetryIntervalMs", 100);
    char data[512];
    int elapsed_ms = 0;
    int phase;

    if (timeout_ms < 30000) timeout_ms = 30000;
    if (indicator_led_open(&device) != 0) {
        send_report(fd, "indicator_led", "failed", 4600, "Unable to open indicator LED controls", "{}");
        return -1;
    }

    if (phase_ms <= 0) phase_ms = 2000;
    if (red_green_overlap_ms < 0) red_green_overlap_ms = 0;
    if (indicator_led_set(&device, INDICATOR_LED_BLUE, 0, &result) != 0 ||
        indicator_led_set(&device, INDICATOR_LED_RED, 0, &result) != 0 ||
        indicator_led_set_charge(false, i2c_timeout_ms, retry_interval_ms) != 0) {
        indicator_led_close(&device);
        send_report(fd, "indicator_led", "failed", 4602, "Unable to initialize RGB LED test", "{}");
        return -1;
    }

    for (phase = 0; phase < INDICATOR_LED_PHASE_COUNT; ++phase) {
        const char *current_led = phase == 0 ? "red" : (phase == 1 ? "green" : "blue");
        fd_set read_fds;
        struct timeval tv;
        char line[PROTOCOL_MAX_LINE];
        int ready;

        if ((phase == 0 &&
             (indicator_led_set_charge(false, i2c_timeout_ms, retry_interval_ms) != 0 ||
              indicator_led_set(&device, INDICATOR_LED_BLUE, 0, &result) != 0 ||
              indicator_led_set(&device, INDICATOR_LED_RED, INDICATOR_LED_ON_BRIGHTNESS, &result) != 0)) ||
            (phase == 1 &&
             (indicator_led_set_charge(true, i2c_timeout_ms, retry_interval_ms) != 0 ||
              indicator_led_set(&device, INDICATOR_LED_BLUE, 0, &result) != 0)) ||
            (phase == 2 &&
             (indicator_led_set(&device, INDICATOR_LED_BLUE, INDICATOR_LED_ON_BRIGHTNESS, &result) != 0 ||
              indicator_led_set(&device, INDICATOR_LED_RED, 0, &result) != 0 ||
              indicator_led_set_charge(false, i2c_timeout_ms, retry_interval_ms) != 0))) {
            indicator_led_set(&device, INDICATOR_LED_BLUE, 0, &result);
            indicator_led_set(&device, INDICATOR_LED_RED, 0, &result);
            indicator_led_set_charge(false, i2c_timeout_ms, retry_interval_ms);
            indicator_led_close(&device);
            snprintf(data, sizeof(data), "{\"currentLed\":\"%s\"}", current_led);
            send_report(fd, "indicator_led", "failed", 4601, "Unable to set RGB LED phase", data);
            return -1;
        }
        if (phase == 1) {
            sleep_ms_local(red_green_overlap_ms);
            if (indicator_led_set(&device, INDICATOR_LED_RED, 0, &result) != 0) {
                indicator_led_set(&device, INDICATOR_LED_BLUE, 0, &result);
                indicator_led_set_charge(false, i2c_timeout_ms, retry_interval_ms);
                indicator_led_close(&device);
                send_report(fd, "indicator_led", "failed", 4601, "Unable to switch red LED off", "{}");
                return -1;
            }
        }
        snprintf(data, sizeof(data),
                 "{\"manualObserved\":true,\"requiresOperatorDecision\":true,\"displayMode\":\"rgb_sequence\","
                 "\"phaseDurationMs\":%d,\"currentLed\":\"%s\",\"phaseIndex\":%d,\"phaseCount\":%d,"
                 "\"redGreenOverlapMs\":%d,\"timeoutMs\":%d}",
                 phase_ms, current_led, phase + 1, INDICATOR_LED_PHASE_COUNT,
                 red_green_overlap_ms, timeout_ms);
        send_report(fd, "indicator_led", "running", 0,
                    phase == 0 ? "Red LED is on; observe for 2 seconds" :
                    phase == 1 ? "Green LED is on; observe for 2 seconds" :
                                 "Blue LED is on; observe for 2 seconds",
                    data);

        FD_ZERO(&read_fds);
        FD_SET(fd, &read_fds);
        tv.tv_sec = phase_ms / 1000;
        tv.tv_usec = (phase_ms % 1000) * 1000;
        ready = select(fd + 1, &read_fds, NULL, NULL, &tv);
        if (ready < 0) break;
        if (ready > 0 && protocol_read_line(fd, line, sizeof(line)) > 0 &&
            (strstr(line, "\"event\":\"operator.decision\"") != NULL ||
             strstr(line, "\"event\":\"test.decision\"") != NULL) &&
            strstr(line, "indicator_led") != NULL) {
            int passed = strstr(line, "\"passed\":true") != NULL;
            indicator_led_set(&device, INDICATOR_LED_BLUE, 0, &result);
            indicator_led_set(&device, INDICATOR_LED_RED, 0, &result);
            indicator_led_set_charge(false, i2c_timeout_ms, retry_interval_ms);
            indicator_led_close(&device);
            snprintf(data, sizeof(data),
                     "{\"manualObserved\":true,\"operatorConfirmed\":%s,\"displayMode\":\"rgb_sequence\",\"phaseDurationMs\":%d}",
                     passed ? "true" : "false", phase_ms);
            return send_report(fd, "indicator_led", passed ? "passed" : "failed",
                               passed ? 0 : 3910,
                               passed ? "Operator confirmed pass" : "Operator confirmed fail", data) == 0 && passed ? 0 : -1;
        }
        elapsed_ms += phase_ms;
    }

    indicator_led_set(&device, INDICATOR_LED_BLUE, 0, &result);
    indicator_led_set_charge(false, i2c_timeout_ms, retry_interval_ms);
    indicator_led_set(&device, INDICATOR_LED_RED, 0, &result);

    while (elapsed_ms < timeout_ms) {
        fd_set read_fds;
        struct timeval tv;
        char line[PROTOCOL_MAX_LINE];
        int ready;
        int wait_ms = timeout_ms - elapsed_ms;

        if (wait_ms > 1000) wait_ms = 1000;
        FD_ZERO(&read_fds);
        FD_SET(fd, &read_fds);
        tv.tv_sec = wait_ms / 1000;
        tv.tv_usec = (wait_ms % 1000) * 1000;
        ready = select(fd + 1, &read_fds, NULL, NULL, &tv);
        if (ready < 0) break;
        if (ready > 0 && protocol_read_line(fd, line, sizeof(line)) > 0 &&
            (strstr(line, "\"event\":\"operator.decision\"") != NULL ||
             strstr(line, "\"event\":\"test.decision\"") != NULL) &&
            strstr(line, "indicator_led") != NULL) {
            int passed = strstr(line, "\"passed\":true") != NULL;
            indicator_led_close(&device);
            snprintf(data, sizeof(data),
                     "{\"manualObserved\":true,\"operatorConfirmed\":%s,\"displayMode\":\"rgb_sequence\",\"phaseDurationMs\":%d}",
                     passed ? "true" : "false", phase_ms);
            return send_report(fd, "indicator_led", passed ? "passed" : "failed",
                               passed ? 0 : 3910,
                               passed ? "Operator confirmed pass" : "Operator confirmed fail", data) == 0 && passed ? 0 : -1;
        }
        elapsed_ms += wait_ms;
    }

    indicator_led_close(&device);
    send_report(fd, "indicator_led", "failed", 3911, "Operator decision timed out", "{\"displayMode\":\"rgb_sequence\"}");
    return -1;
#undef INDICATOR_LED_ON_BRIGHTNESS
#undef INDICATOR_LED_PHASE_COUNT
}

static int run_recovery_adc(int fd, const char *test_start, const char *test_end)
{
    const char *adc_path = "/sys/devices/platform/2ae00000.adc/iio:device0/in_voltage1_raw";
    int threshold = param_int(test_start, test_end, "recoveryPressThreshold", 100);
    int max_raw = param_int(test_start, test_end, "recoveryMaxRaw", 5000);
    int stable_required = param_int(test_start, test_end, "recoveryStableSampleCount", 3);
    int sample_interval_ms = param_int(test_start, test_end, "recoverySampleIntervalMs", 100);
    int timeout_ms = param_int(test_start, test_end, "recoveryTimeoutMs", 10000);
    int stable_count = 0;
    int elapsed_ms = 0;
    char data[512];

    if (stable_required <= 0) stable_required = 3;
    if (sample_interval_ms <= 0) sample_interval_ms = 100;
    if (timeout_ms <= 0) timeout_ms = 10000;
    snprintf(data, sizeof(data),
             "{\"phase\":\"recovery\",\"adcPath\":\"%s\",\"rawValue\":-1,\"pressThreshold\":%d,\"maxRaw\":%d,\"stableCount\":0,\"stableRequired\":%d,\"timeoutMs\":%d}",
             adc_path, threshold, max_raw, stable_required, timeout_ms);
    send_report(fd, "keys", "running", 0, "Please press Recovery key", data);

    while (elapsed_ms <= timeout_ms) {
        FILE *file = fopen(adc_path, "r");
        int raw_value = -1;
        if (file != NULL) {
            if (fscanf(file, "%d", &raw_value) != 1) raw_value = -1;
            fclose(file);
        }

        if (raw_value >= 0 && raw_value <= max_raw && raw_value < threshold) stable_count++;
        else stable_count = 0;
        snprintf(data, sizeof(data),
                 "{\"phase\":\"recovery\",\"adcPath\":\"%s\",\"rawValue\":%d,\"pressThreshold\":%d,\"maxRaw\":%d,\"stableCount\":%d,\"stableRequired\":%d,\"elapsedMs\":%d,\"timeoutMs\":%d}",
                 adc_path, raw_value, threshold, max_raw, stable_count, stable_required, elapsed_ms, timeout_ms);
        if (stable_count >= stable_required) {
            return send_report(fd, "keys", "passed", 0, "Recovery key detected", data);
        }
        send_report(fd, "keys", "running", 0, "Waiting for Recovery key ADC threshold", data);
        sleep_ms_local(sample_interval_ms);
        elapsed_ms += sample_interval_ms;
    }

    send_report(fd, "keys", "failed", 4003, "Recovery key was not detected", data);
    return -1;
}

static int run_keys(int fd, const struct app_config *config, const char *test_start, const char *test_end)
{
    const uint32_t expected = (1U << (KEY_INPUT_CONFIRM + 1)) - 1U;
    struct key_input input;
    struct key_input_event event;
    struct timespec deadline_start, now;
    uint32_t detected = 0;
    int timeout_ms = param_int(test_start, test_end, "timeoutMs", config->keys_timeout_ms);
    char test_mode[32] = "pcba";
    char data[256];
    param_string(test_start, test_end, "mode", test_mode, sizeof(test_mode));

    if (timeout_ms < 45000) timeout_ms = 45000;
    snprintf(data, sizeof(data),
             "{\"expectedKeys\":[\"up\",\"down\",\"left\",\"right\",\"confirm\"],\"detectedMask\":0,\"expectedMask\":%u,\"timeoutMs\":%d,\"remainingMs\":%d}",
             expected, timeout_ms, timeout_ms);
    send_report(fd, "keys", "running", 0,
                "Press Up, Down, Left, Right, Confirm, then Recovery key",
                data);
    if (key_input_open(&input) != 0) {
        send_report(fd, "keys", "failed", 4000, "Unable to open key input devices", "{}");
        return -1;
    }
    key_input_drain_pending(&input);

    clock_gettime(CLOCK_MONOTONIC, &deadline_start);
    while (detected != expected) {
        int remaining;
        int rc;
        clock_gettime(CLOCK_MONOTONIC, &now);
        remaining = timeout_ms - elapsed_ms(&deadline_start, &now);
        if (remaining <= 0) {
            key_input_close(&input);
            snprintf(data, sizeof(data), "{\"detectedMask\":%u,\"expectedMask\":%u,\"timeoutMs\":%d,\"remainingMs\":0}",
                     detected, expected, timeout_ms);
            send_report(fd, "keys", "failed", 4001, "Five-key test timed out", data);
            return -1;
        }
        rc = key_input_read_event(&input, remaining, &event);
        if (rc < 0) {
            key_input_close(&input);
            send_report(fd, "keys", "failed", 4002, "Unable to read key input event", "{}");
            return -1;
        }
        if (rc == 0 || !event.pressed || event.key == KEY_INPUT_UNKNOWN) continue;
        if ((detected & (1U << event.key)) == 0) {
            detected |= 1U << event.key;
            clock_gettime(CLOCK_MONOTONIC, &deadline_start);
            snprintf(data, sizeof(data),
                     "{\"key\":\"%s\",\"rawCode\":%d,\"detectedMask\":%u,\"expectedMask\":%u,\"timeoutMs\":%d,\"remainingMs\":%d}",
                     key_input_name(event.key), event.raw_code, detected, expected, timeout_ms, timeout_ms);
            send_report(fd, "keys", "running", 0, "Key press detected", data);
        }
    }

    key_input_close(&input);
    snprintf(data, sizeof(data), "{\"detectedMask\":%u,\"expectedMask\":%u}",
             detected, expected);
    if (strcmp(test_mode, "finished_product") == 0) {
        return run_recovery_adc(fd, test_start, test_end);
    }
    return send_report(fd, "keys", "passed", 0, "Five-key test passed", data);
}

static int run_camera(int fd, const struct app_config *config, const char *test_start, const char *test_end)
{
    char device_path[128];
    char exposure_counter_path[160];
    char pwm_status_path[192];
    int wait_camera_timeout_ms = 30000;
    int progress_report_interval_ms = 1000;
    int elapsed_ms = 0;
    struct camera_stream_request request = {
        .device_path = device_path,
        .stream_frame_count = config->camera_stream_frame_count,
        .timeout_ms = 3000,
        .require_exposure_interrupt = config->camera_require_exposure_interrupt != 0,
        .exposure_counter_path = exposure_counter_path,
        .exposure_frame_count = config->camera_exposure_frame_count,
        .require_pwm_pulse = config->camera_require_pwm_pulse != 0,
        .pwm_status_path = pwm_status_path,
        .pwm_min_pulse_delta = config->camera_pwm_min_pulse_delta,
    };
    struct camera_stream_result result;
    char data[1024];

    snprintf(device_path, sizeof(device_path), "%s", config->camera_device_path);
    exposure_counter_path[0] = '\0';
    snprintf(pwm_status_path, sizeof(pwm_status_path), "%s", config->camera_pwm_status_path);
    if (config->camera_exposure_counter_path != NULL) {
        snprintf(exposure_counter_path, sizeof(exposure_counter_path), "%s", config->camera_exposure_counter_path);
    }
    param_string(test_start, test_end, "devicePath", device_path, sizeof(device_path));
    param_string(test_start, test_end, "exposureCounterPath", exposure_counter_path, sizeof(exposure_counter_path));
    param_string(test_start, test_end, "pwmStatusPath", pwm_status_path, sizeof(pwm_status_path));
    request.stream_frame_count = param_int(test_start, test_end, "streamFrameCount", request.stream_frame_count);
    request.timeout_ms = param_int(test_start, test_end, "timeoutMs", request.timeout_ms);
    request.exposure_frame_count = param_int(test_start, test_end, "minInterruptCount", request.exposure_frame_count);
    request.exposure_frame_count = param_int(test_start, test_end, "exposureFrameCount", request.exposure_frame_count);
    request.require_exposure_interrupt = param_bool(test_start, test_end, "requireExposureInterrupt", request.require_exposure_interrupt);
    request.require_pwm_pulse = param_bool(test_start, test_end, "requirePwmPulse", request.require_pwm_pulse);
    request.pwm_min_pulse_delta = param_int(test_start, test_end, "minPwmPulseDelta", request.pwm_min_pulse_delta);
    if (request.stream_frame_count <= 0) request.stream_frame_count = 90;
    if (request.pwm_min_pulse_delta <= 0) request.pwm_min_pulse_delta = 86;
    wait_camera_timeout_ms = param_int(test_start, test_end, "waitCameraTimeoutMs", wait_camera_timeout_ms);
    progress_report_interval_ms = param_int(test_start, test_end, "progressReportIntervalMs", progress_report_interval_ms);
    if (progress_report_interval_ms <= 0) progress_report_interval_ms = 1000;
    if (exposure_counter_path[0] != '\0') request.require_exposure_interrupt = 1;
    memset(&result, 0, sizeof(result));

    while (elapsed_ms < wait_camera_timeout_ms && access(device_path, F_OK) != 0) {
        snprintf(data, sizeof(data),
                 "{\"phase\":\"wait_camera\",\"device\":\"%s\",\"cameraPresent\":false,"
                 "\"requiresCameraInsert\":true,\"waitCameraTimeoutMs\":%d,\"elapsedMs\":%d}",
                 device_path, wait_camera_timeout_ms, elapsed_ms);
        send_report(fd, "typec_camera", "running", 0, "Please insert camera before camera test", data);
        sleep_ms_local(progress_report_interval_ms);
        elapsed_ms += progress_report_interval_ms;
    }

    if (access(device_path, F_OK) != 0) {
        snprintf(data, sizeof(data),
                 "{\"phase\":\"wait_camera\",\"device\":\"%s\",\"cameraPresent\":false,"
                 "\"requiresCameraInsert\":true,\"waitCameraTimeoutMs\":%d,\"elapsedMs\":%d,"
                 "\"failureReason\":\"camera_not_inserted\"}",
                 device_path, wait_camera_timeout_ms, wait_camera_timeout_ms);
        return send_report(fd, "typec_camera", "failed", 4706, "Camera insert timeout", data);
    }

    snprintf(data, sizeof(data),
             "{\"phase\":\"camera_detected\",\"device\":\"%s\",\"cameraPresent\":true,"
             "\"requiresCameraInsert\":false,\"waitCameraTimeoutMs\":%d,\"elapsedMs\":%d}",
             device_path, wait_camera_timeout_ms, elapsed_ms);
    send_report(fd, "typec_camera", "running", 0, "Camera detected, starting stream test", data);

    if (camera_stream_run_test(&request, &result) != 0) {
        snprintf(data, sizeof(data),
                 "{\"phase\":\"failed\",\"device\":\"%s\",\"capturedFrames\":%d,\"exposureDelta\":%d,"
                 "\"pwmStatusPath\":\"%s\",\"pwmPulseCountBefore\":%llu,\"pwmPulseCountAfter\":%llu,"
                 "\"pwmPulseDelta\":%llu,\"pwmMonoNs\":%lld,\"pwmRtcNs\":%lld,\"pwmOk\":%s,"
                 "\"streamOk\":%s,\"exposureOk\":%s,\"requiredExposureFrames\":%d,\"requiredPwmPulseDelta\":%d}",
                 result.device_path, result.captured_frames, result.exposure_delta,
                 pwm_status_path,
                 result.pwm_pulse_count_before, result.pwm_pulse_count_after,
                 result.pwm_pulse_delta, result.pwm_mono_ns, result.pwm_rtc_ns,
                 result.pwm_ok ? "true" : "false",
                 result.stream_ok ? "true" : "false",
                 result.exposure_ok ? "true" : "false",
                 request.exposure_frame_count,
                 request.pwm_min_pulse_delta);
        send_report(fd, "typec_camera", "failed",
                    result.error_code == 0 ? 4700 : result.error_code,
                    result.message[0] == '\0' ? "Camera stream test failed" : result.message,
                    data);
        return -1;
    }
    snprintf(data, sizeof(data),
             "{\"phase\":\"completed\",\"device\":\"%s\",\"capturedFrames\":%d,\"exposureDelta\":%d,"
             "\"pwmStatusPath\":\"%s\",\"pwmPulseCountBefore\":%llu,\"pwmPulseCountAfter\":%llu,"
             "\"pwmPulseDelta\":%llu,\"pwmMonoNs\":%lld,\"pwmRtcNs\":%lld,\"pwmOk\":%s,"
             "\"streamOk\":%s,\"exposureOk\":%s,\"requiredExposureFrames\":%d,\"requiredPwmPulseDelta\":%d}",
             result.device_path, result.captured_frames, result.exposure_delta,
             pwm_status_path,
             result.pwm_pulse_count_before, result.pwm_pulse_count_after,
             result.pwm_pulse_delta, result.pwm_mono_ns, result.pwm_rtc_ns,
             result.pwm_ok ? "true" : "false",
             result.stream_ok ? "true" : "false",
             result.exposure_ok ? "true" : "false",
             request.exposure_frame_count,
             request.pwm_min_pulse_delta);
    return send_report(fd, "typec_camera", "passed", 0, result.message, data);
}

static int send_completed(int fd, const char *session_id, const char *status,
                          int code, const char *message)
{
    char line[1024];
    protocol_build_session_completed(line, sizeof(line), session_id, status, code, message);
    return protocol_write_line(fd, line);
}

static void remember_failure(int *failed_count, int *first_failed_code,
                             char *first_failed_test, size_t first_failed_test_size,
                             int code, const char *test_id)
{
    size_t length;
    ++(*failed_count);
    if (*first_failed_code == 0) {
        *first_failed_code = code;
        length = strnlen(test_id, first_failed_test_size - 1);
        memcpy(first_failed_test, test_id, length);
        first_failed_test[length] = '\0';
    }
}

static int run_unsupported_test(int fd, const char *test_id)
{
    char data[256];
    snprintf(data, sizeof(data), "{\"unsupported\":true,\"testId\":\"%s\"}", test_id);
    send_report(fd, test_id, "running", 0, "Test is not implemented on 3576 yet", data);
    send_report(fd, test_id, "failed", 3900, "Test is not implemented on 3576 yet", data);
    return -1;
}

static int run_skipped_test(int fd, const char *test_id, const char *test_start, const char *test_end)
{
    char reason[160];
    char data[512];
    snprintf(reason, sizeof(reason), "Skipped by host policy");
    param_string(test_start, test_end, "skipReason", reason, sizeof(reason));
    snprintf(data, sizeof(data),
             "{\"skipReason\":\"%s\",\"countInFinalVerdict\":false}",
             reason);
    return send_report(fd, test_id, "skipped", 2900, reason, data);
}

static int run_one_test(int fd, const char *test_id, const struct app_config *config,
                        const char *test_start, const char *test_end)
{
    char test_mode[32] = "pcba";
    param_string(test_start, test_end, "mode", test_mode, sizeof(test_mode));
    if (strcmp(test_id, "board_state") == 0) return run_board_state(fd);
    if (strcmp(test_id, "emmc") == 0 || strcmp(test_id, "ddr") == 0) return run_emmc_ddr(fd, test_id, test_start, test_end);
    if (strcmp(test_id, "hdmi") == 0) return run_manual_observation(fd, "hdmi", "HDMI", test_start, test_end);
    if (strcmp(test_id, "lcd") == 0) return run_manual_observation(fd, "lcd", "LCD", test_start, test_end);
    if (strcmp(test_id, "reset_button") == 0) return run_manual_observation(fd, "reset_button", "Reset button and LCD off state", test_start, test_end);
    if (strcmp(test_id, "fan") == 0 && strcmp(test_mode, "finished_product") == 0) return run_finished_product_fan(fd, test_start, test_end);
    if (strcmp(test_id, "fan") == 0) return run_skipped_test(fd, "fan", test_start, test_end);
    if (strcmp(test_id, "ethernet_led") == 0) return run_ethernet_led(fd, test_start, test_end);
    if (strcmp(test_id, "indicator_led") == 0 && strcmp(test_mode, "finished_product") == 0) {
        return run_finished_product_indicator_led(fd, test_start, test_end);
    }
    if (strcmp(test_id, "fingerprint") == 0) return run_fingerprint(fd);
    if (strcmp(test_id, "ethernet") == 0) return run_ethernet(fd, test_start, test_end);
    if (strcmp(test_id, "wifi") == 0) return run_wifi(fd, config, test_start, test_end);
    if (strcmp(test_id, "tf") == 0) return run_tf_card(fd, config, test_start, test_end);
    if (strcmp(test_id, "usb2") == 0) return run_usb_variant(fd, test_start, test_end, 2);
    if (strcmp(test_id, "usb3") == 0) return run_usb_variant(fd, test_start, test_end, 3);
    if (strcmp(test_id, "usb2_3") == 0) return run_usb2_3(fd, test_start, test_end);
    if (strcmp(test_id, "pcba_test_points") == 0) return run_pcba_test_points(fd, test_start, test_end);
    if (strcmp(test_id, "bluetooth") == 0) return run_bluetooth(fd, config, test_start, test_end);
    if (strcmp(test_id, "battery_management") == 0) return run_battery_management(fd, test_start, test_end);
    if (strcmp(test_id, "typec_fast_charge") == 0 || strcmp(test_id, "fast_charge") == 0) {
        return run_fast_charge(fd, config, test_start, test_end);
    }
    if (strcmp(test_id, "keys") == 0) return run_keys(fd, config, test_start, test_end);
    if (strcmp(test_id, "typec_camera") == 0 || strcmp(test_id, "camera") == 0) {
        return run_camera(fd, config, test_start, test_end);
    }
    return run_unsupported_test(fd, test_id);
}

static int failure_code_for_test(const char *test_id)
{
    if (strcmp(test_id, "board_state") == 0) return 3001;
    if (strcmp(test_id, "emmc") == 0) return 3016;
    if (strcmp(test_id, "ddr") == 0) return 3017;
    if (strcmp(test_id, "fingerprint") == 0) return 3002;
    if (strcmp(test_id, "ethernet") == 0) return 3011;
    if (strcmp(test_id, "ethernet_led") == 0) return 3015;
    if (strcmp(test_id, "wifi") == 0) return 3003;
    if (strcmp(test_id, "tf") == 0) return 3004;
    if (strcmp(test_id, "usb2") == 0 || strcmp(test_id, "usb2_3") == 0) return 3012;
    if (strcmp(test_id, "usb3") == 0) return 3016;
    if (strcmp(test_id, "pcba_test_points") == 0) return 3013;
    if (strcmp(test_id, "bluetooth") == 0) return 3005;
    if (strcmp(test_id, "battery_management") == 0) return 3014;
    if (strcmp(test_id, "typec_fast_charge") == 0 || strcmp(test_id, "fast_charge") == 0) return 3006;
    if (strcmp(test_id, "hdmi") == 0) return 3009;
    if (strcmp(test_id, "lcd") == 0) return 3010;
    if (strcmp(test_id, "keys") == 0) return 3007;
    if (strcmp(test_id, "typec_camera") == 0 || strcmp(test_id, "camera") == 0) return 3008;
    return 3900;
}

int test_runner_run_plan(int fd, const char *session_id, const char *request_json,
                         const struct app_config *config)
{
    int executed = 0;
    int failed_count = 0;
    int skipped_count = 0;
    int first_failed_code = 0;
    char first_failed_test[80] = "";
    const char *cursor = request_json;
    const char *test_start;
    const char *test_end;
    char test_id[80];
    char session_start_time[40];
    char session_end_time[40];
    int rc;

    format_timestamp_now(session_start_time, sizeof(session_start_time));

    while (read_next_test(&cursor, test_id, sizeof(test_id), &test_start, &test_end)) {
        executed++;
        if (param_bool(test_start, test_end, "skip", 0)) {
            run_skipped_test(fd, test_id, test_start, test_end);
            skipped_count++;
            continue;
        }
        rc = run_one_test(fd, test_id, config, test_start, test_end);
        if (rc == -2) {
            return -2;
        }
        if (rc != 0) {
            remember_failure(&failed_count, &first_failed_code,
                             first_failed_test, sizeof(first_failed_test),
                             failure_code_for_test(test_id), test_id);
        }
    }

    format_timestamp_now(session_end_time, sizeof(session_end_time));

    if (executed == 0) {
        if (strstr(request_json, "\"tests\"") != NULL) {
            if (config != NULL) {
                board_state_record_session_result(config->board_state_path, session_id,
                                                  session_start_time, session_end_time, "Fail");
            }
            send_report(fd, "unknown", "failed", 3000, "No supported test id found", "{}");
            return send_completed(fd, session_id, "failed", 3000, "No supported test id found");
        }
        run_board_state(fd);
    }

    if (failed_count > 0) {
        char message[160];
        snprintf(message, sizeof(message), "Session completed with %d failed test(s), %d skipped, first failed: %s",
                 failed_count, skipped_count, first_failed_test);
        if (config != NULL) {
            board_state_record_session_result(config->board_state_path, session_id,
                                              session_start_time, session_end_time, "Fail");
        }
        return send_completed(fd, session_id, "failed", first_failed_code, message);
    }
    if (skipped_count > 0) {
        char message[160];
        snprintf(message, sizeof(message), "Session completed with %d skipped test(s)", skipped_count);
        if (config != NULL) {
            board_state_record_session_result(config->board_state_path, session_id,
                                              session_start_time, session_end_time, "Pass");
        }
        return send_completed(fd, session_id, "passed", 0, message);
    }
    if (config != NULL) {
        board_state_record_session_result(config->board_state_path, session_id,
                                          session_start_time, session_end_time, "Pass");
    }
    return send_completed(fd, session_id, "passed", 0, "Session completed");
}
