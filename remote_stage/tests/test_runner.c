#define _POSIX_C_SOURCE 200809L
#include "test_runner.h"

#include "../hardware/fingerprint/fingerprint.h"
#include "../hardware/bluetooth/bluetoothctl_scan.h"
#include "../hardware/camera/camera_stream.h"
#include "../hardware/ethernet/ethernet_nmcli.h"
#include "../hardware/fast_charge/fast_charge.h"
#include "../hardware/keys/key_input.h"
#include "../hardware/tf_card/tf_card.h"
#include "../hardware/usb3.0/usb3_file_check.h"
#include "../hardware/pcba_points/pcba_points_file.h"
#include "../hardware/wifi/wifi_nmcli.h"
#include "../protocol/protocol.h"
#include "../storage/board_state.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/select.h>
#include <time.h>
#include <unistd.h>

#define CHARGE_CONTROL_ENABLE_COMMAND "i2ctransfer -f -y 7 w2@0x6b 0x12 0x00"
#define CHARGE_CONTROL_DISABLE_COMMAND "i2ctransfer -f -y 7 w2@0x6b 0x12 0x80"
#define CHARGE_CURRENT_LIMIT_500MA_COMMAND "i2ctransfer -f -y 7 w3@0x6b 0x03 0x00 0x32"
#define CHARGE_CURRENT_LIMIT_MA 500
#define PMIC_STATUS0_READ_COMMAND "i2ctransfer -f -y 7 w1@0x6b 0x1b r1"
#define PMIC_STATUS1_READ_COMMAND "i2ctransfer -f -y 7 w1@0x6b 0x1c r1"

static int wait_test_decision(int fd, const char *test_id, int timeout_ms, int *passed);

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

static int any_camera_device_present(void)
{
    int index;
    char path[32];
    for (index = 0; index < 10; ++index) {
        snprintf(path, sizeof(path), "/dev/video%d", index);
        if (access(path, F_OK) == 0) {
            return 1;
        }
    }
    return 0;
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
    int min_rssi = -75;
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
                     "\"scanTimeoutMs\":%d,\"wifiEnabled\":%s,\"found\":false,\"rssi\":%d,"
                     "\"failureReason\":\"%s\"}",
                     ssid, device.interface_name, attempt, max_retry_count,
                     scan_timeout_ms, result.wifi_enabled ? "true" : "false", result.rssi,
                     result.failure_reason);
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

static int run_usb2_3(int fd, const char *test_start, const char *test_end)
{
    char record_file[160] = "/tmp/spacetest_usb_ports.json";
    struct usb_ports_request request = {
        .record_file = record_file,
        .expected_usb2_count = 2,
        .expected_usb3_count = 2,
        .timeout_ms = 3000,
    };
    struct usb_ports_result result;
    char data[768];

    param_string(test_start, test_end, "recordFile", record_file, sizeof(record_file));
    request.expected_usb2_count = param_int(test_start, test_end, "expectedUsb2Count", request.expected_usb2_count);
    request.expected_usb3_count = param_int(test_start, test_end, "expectedUsb3Count", request.expected_usb3_count);
    request.timeout_ms = param_int(test_start, test_end, "timeoutMs", request.timeout_ms);

    snprintf(data, sizeof(data),
             "{\"recordFile\":\"%s\",\"expectedUsb2Count\":%d,\"expectedUsb3Count\":%d}",
             record_file, request.expected_usb2_count, request.expected_usb3_count);
    send_report(fd, "usb2_3", "running", 0, "Read USB2.0&3.0 summary file", data);

    if (usb_ports_run_test(&request, &result) != 0) {
        snprintf(data, sizeof(data),
                 "{\"recordFile\":\"%s\",\"usb2Count\":%d,\"usb3Count\":%d,\"expectedUsb2Count\":%d,\"expectedUsb3Count\":%d}",
                 result.record_file, result.usb2_count, result.usb3_count,
                 result.expected_usb2_count, result.expected_usb3_count);
        send_report(fd, "usb2_3", "failed",
                    result.error_code == 0 ? 4900 : result.error_code,
                    result.message[0] == '\0' ? "USB2.0&3.0 record check failed" : result.message,
                    data);
        return -1;
    }

    snprintf(data, sizeof(data),
             "{\"recordFile\":\"%s\",\"usb2Count\":%d,\"usb3Count\":%d,\"expectedUsb2Count\":%d,\"expectedUsb3Count\":%d}",
             result.record_file, result.usb2_count, result.usb3_count,
             result.expected_usb2_count, result.expected_usb3_count);
    return send_report(fd, "usb2_3", "passed", 0, result.message, data);
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
    int ethernet_link_up = 0;
    int camera_present = 0;
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

    ethernet_link_up = net_carrier_is_up("end0");
    camera_present = any_camera_device_present();
    while (ready_elapsed_ms < wait_ready_timeout_ms && (ethernet_link_up || camera_present)) {
        snprintf(data, sizeof(data),
                 "{\"phase\":\"wait_ready\",\"waitReadyTimeoutMs\":%d,\"elapsedMs\":%d,"
                 "\"ethernetLinkUp\":%s,\"cameraPresent\":%s,"
                 "\"requiresEthernetUnplug\":%s,\"requiresCameraUnplug\":%s}",
                 wait_ready_timeout_ms, ready_elapsed_ms,
                 ethernet_link_up ? "true" : "false",
                 camera_present ? "true" : "false",
                 ethernet_link_up ? "true" : "false",
                 camera_present ? "true" : "false");
        send_report(fd, "typec_fast_charge", "running", 0,
                    "Please unplug Ethernet cable and camera before fast charge test", data);
        sleep_ms_local(progress_report_interval_ms);
        ready_elapsed_ms += progress_report_interval_ms;
        ethernet_link_up = net_carrier_is_up("end0");
        camera_present = any_camera_device_present();
    }

    if (ethernet_link_up || camera_present) {
        snprintf(data, sizeof(data),
                 "{\"phase\":\"wait_ready\",\"waitReadyTimeoutMs\":%d,\"elapsedMs\":%d,"
                 "\"ethernetLinkUp\":%s,\"cameraPresent\":%s,"
                 "\"requiresEthernetUnplug\":%s,\"requiresCameraUnplug\":%s,"
                 "\"failureReason\":\"external_load_not_removed\"}",
                 wait_ready_timeout_ms, wait_ready_timeout_ms,
                 ethernet_link_up ? "true" : "false",
                 camera_present ? "true" : "false",
                 ethernet_link_up ? "true" : "false",
                 camera_present ? "true" : "false");
        return send_report(fd, "typec_fast_charge", "failed", 4406,
                           "Please unplug Ethernet cable and camera before fast charge test", data);
    }

    snprintf(data, sizeof(data),
             "{\"phase\":\"ready\",\"waitReadyTimeoutMs\":%d,\"elapsedMs\":%d,"
             "\"ethernetLinkUp\":false,\"cameraPresent\":false,"
             "\"requiresEthernetUnplug\":false,\"requiresCameraUnplug\":false}",
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
    char data[512];
    int timeout_ms = 15000;
    int wait_ready_timeout_ms = 120000;
    int progress_report_interval_ms = 1000;
    int elapsed_ms = 0;
    int ethernet_link_up = 0;
    int camera_present = 0;
    int passed = 0;

    timeout_ms = param_int(test_start, test_end, "timeoutMs", timeout_ms);
    wait_ready_timeout_ms = param_int(test_start, test_end, "waitReadyTimeoutMs", wait_ready_timeout_ms);
    progress_report_interval_ms = param_int(test_start, test_end, "progressReportIntervalMs", progress_report_interval_ms);
    if (progress_report_interval_ms <= 0) progress_report_interval_ms = 1000;

    ethernet_link_up = net_carrier_is_up("end0");
    camera_present = any_camera_device_present();
    while (elapsed_ms < wait_ready_timeout_ms && (ethernet_link_up || camera_present)) {
        snprintf(data, sizeof(data),
                 "{\"phase\":\"wait_ready\",\"waitReadyTimeoutMs\":%d,\"elapsedMs\":%d,"
                 "\"ethernetLinkUp\":%s,\"cameraPresent\":%s,"
                 "\"requiresEthernetUnplug\":%s,\"requiresCameraUnplug\":%s}",
                 wait_ready_timeout_ms, elapsed_ms,
                 ethernet_link_up ? "true" : "false",
                 camera_present ? "true" : "false",
                 ethernet_link_up ? "true" : "false",
                 camera_present ? "true" : "false");
        send_report(fd, "battery_management", "running", 0,
                    "Please unplug Ethernet cable and camera before battery discharge test", data);
        sleep_ms_local(progress_report_interval_ms);
        elapsed_ms += progress_report_interval_ms;
        ethernet_link_up = net_carrier_is_up("end0");
        camera_present = any_camera_device_present();
    }

    if (ethernet_link_up || camera_present) {
        snprintf(data, sizeof(data),
                 "{\"phase\":\"wait_ready\",\"waitReadyTimeoutMs\":%d,\"elapsedMs\":%d,"
                 "\"ethernetLinkUp\":%s,\"cameraPresent\":%s,"
                 "\"requiresEthernetUnplug\":%s,\"requiresCameraUnplug\":%s,"
                 "\"failureReason\":\"external_load_not_removed\"}",
                 wait_ready_timeout_ms, wait_ready_timeout_ms,
                 ethernet_link_up ? "true" : "false",
                 camera_present ? "true" : "false",
                 ethernet_link_up ? "true" : "false",
                 camera_present ? "true" : "false");
        return send_report(fd, "battery_management", "failed", 4705,
                           "Please unplug Ethernet cable and camera before battery discharge test", data);
    }

    snprintf(data, sizeof(data),
             "{\"phase\":\"ready\",\"waitReadyTimeoutMs\":%d,\"elapsedMs\":%d,"
             "\"ethernetLinkUp\":false,\"cameraPresent\":false,"
             "\"requiresEthernetUnplug\":false,\"requiresCameraUnplug\":false}",
             wait_ready_timeout_ms, elapsed_ms);
    send_report(fd, "battery_management", "running", 0,
                "External loads removed, enabling battery discharge mode", data);

    if (set_charge_enabled(0) != 0) {
        snprintf(data, sizeof(data),
                 "{\"chargeControlCommand\":\"disable_charge\",\"chargeControlOk\":false,"
                 "\"pmicCommunicationOk\":false,\"readyForHostDecision\":false}");
        return send_report(fd, "battery_management", "failed", 4701,
                           "Unable to disable charge before battery discharge test", data);
    }

    snprintf(data, sizeof(data),
             "{\"phase\":\"ready_for_host_decision\",\"chargeControlCommand\":\"disable_charge\",\"chargeControlOk\":true,"
             "\"pmicCommunicationOk\":true,\"readyForHostDecision\":true,\"samplingDurationMs\":%d}",
             timeout_ms);
    send_report(fd, "battery_management", "running", 0, "Battery discharge mode enabled, waiting for host decision", data);
    switch (wait_test_decision(fd, "battery_management", timeout_ms, &passed)) {
    case 1:
        return send_report(fd, "battery_management", passed ? "passed" : "failed",
                           passed ? 0 : 4702,
                           passed ? "Host confirmed discharge pass" : "Host confirmed discharge fail",
                           data);
    case 0:
        return send_report(fd, "battery_management", "failed", 4703, "Host decision timed out", data);
    default:
        return send_report(fd, "battery_management", "failed", 4704, "Unable to read host decision", data);
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
    send_report(fd, test_id, "running", 0, "Waiting for operator decision", data);

    switch (wait_operator_decision(fd, test_id, timeout_ms, &passed)) {
    case 1:
        snprintf(data, sizeof(data),
                 "{\"manualObserved\":true,\"operatorConfirmed\":%s}",
                 passed ? "true" : "false");
        return send_report(fd, test_id, passed ? "passed" : "failed",
                           passed ? 0 : 3910,
                           passed ? "Operator confirmed pass" : "Operator confirmed fail",
                           data) == 0 && passed ? 0 : -1;
    case 0:
        snprintf(data, sizeof(data),
                 "{\"manualObserved\":true,\"operatorConfirmed\":false,\"timeoutMs\":%d}",
                 timeout_ms);
        send_report(fd, test_id, "failed", 3911, "Operator decision timed out", data);
        return -1;
    default:
        send_report(fd, test_id, "failed", 3912, "Unable to read operator decision", "{}");
        return -1;
    }
}

static int run_keys(int fd, const struct app_config *config, const char *test_start, const char *test_end)
{
    const uint32_t expected = (1U << (KEY_INPUT_CONFIRM + 1)) - 1U;
    struct key_input input;
    struct key_input_event event;
    struct timespec deadline_start, now;
    uint32_t detected = 0;
    int timeout_ms = param_int(test_start, test_end, "timeoutMs", config->keys_timeout_ms);
    char data[256];

    if (timeout_ms < 45000) timeout_ms = 45000;
    snprintf(data, sizeof(data),
             "{\"expectedKeys\":[\"up\",\"down\",\"left\",\"right\",\"confirm\"],\"detectedMask\":0,\"expectedMask\":%u,\"timeoutMs\":%d,\"remainingMs\":%d}",
             expected, timeout_ms, timeout_ms);
    send_report(fd, "keys", "running", 0,
                "Press Up, Down, Left, Right and Confirm within 45 seconds",
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
    if (strcmp(test_id, "board_state") == 0) return run_board_state(fd);
    if (strcmp(test_id, "hdmi") == 0) return run_manual_observation(fd, "hdmi", "HDMI", test_start, test_end);
    if (strcmp(test_id, "lcd") == 0) return run_manual_observation(fd, "lcd", "LCD", test_start, test_end);
    if (strcmp(test_id, "fingerprint") == 0) return run_fingerprint(fd);
    if (strcmp(test_id, "ethernet") == 0) return run_ethernet(fd, test_start, test_end);
    if (strcmp(test_id, "wifi") == 0) return run_wifi(fd, config, test_start, test_end);
    if (strcmp(test_id, "tf") == 0) return run_tf_card(fd, config, test_start, test_end);
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
    if (strcmp(test_id, "fingerprint") == 0) return 3002;
    if (strcmp(test_id, "ethernet") == 0) return 3011;
    if (strcmp(test_id, "wifi") == 0) return 3003;
    if (strcmp(test_id, "tf") == 0) return 3004;
    if (strcmp(test_id, "usb2_3") == 0) return 3012;
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

    format_timestamp_now(session_start_time, sizeof(session_start_time));

    while (read_next_test(&cursor, test_id, sizeof(test_id), &test_start, &test_end)) {
        executed++;
        if (param_bool(test_start, test_end, "skip", 0)) {
            run_skipped_test(fd, test_id, test_start, test_end);
            skipped_count++;
            continue;
        }
        if (run_one_test(fd, test_id, config, test_start, test_end) != 0) {
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
