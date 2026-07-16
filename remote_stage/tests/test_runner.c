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
#include <string.h>
#include <sys/select.h>
#include <time.h>
#include <unistd.h>

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

static int send_report(int fd, const char *test_id, const char *status,
                       int code, const char *message, const char *data_json)
{
    char line[16384];
    protocol_build_test_report(line, sizeof(line), test_id, status, code, message, data_json);
    return protocol_write_line(fd, line);
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
    char router_ip[64];
    struct wifi_request request = {
        .ssid = ssid,
        .password = NULL,
        .router_ip = router_ip,
        .ping_count = 4,
        .timeout_ms = 5000,
        .reuse_current_connection = false,
        .ethernet_interface_name = "end0",
        .wait_ethernet_unplug = true,
        .unplug_timeout_ms = 10000,
    };
    struct wifi_result result;
    char data[1024];

    snprintf(ssid, sizeof(ssid), "%s", config->wifi_ssid);
    snprintf(router_ip, sizeof(router_ip), "%s", config->wifi_router_ip);
    param_string(test_start, test_end, "ssid", ssid, sizeof(ssid));
    param_string(test_start, test_end, "password", request.password_buffer, sizeof(request.password_buffer));
    if (request.password_buffer[0] != '\0') request.password = request.password_buffer;
    param_string(test_start, test_end, "routerIp", router_ip, sizeof(router_ip));
    request.ping_count = param_int(test_start, test_end, "pingCount", request.ping_count);
    request.timeout_ms = param_int(test_start, test_end, "timeoutMs", request.timeout_ms);
    param_string(test_start, test_end, "ethernetInterfaceName", request.ethernet_interface_name_buffer, sizeof(request.ethernet_interface_name_buffer));
    if (request.ethernet_interface_name_buffer[0] != '\0') request.ethernet_interface_name = request.ethernet_interface_name_buffer;
    request.wait_ethernet_unplug = param_bool(test_start, test_end, "waitEthernetUnplug", request.wait_ethernet_unplug);
    request.unplug_timeout_ms = param_int(test_start, test_end, "unplugTimeoutMs", request.unplug_timeout_ms);
    memset(&result, 0, sizeof(result));
    snprintf(data, sizeof(data),
             "{\"ssid\":\"%s\",\"routerIp\":\"%s\",\"interfaceName\":\"%s\",\"waitEthernetUnplug\":%s,\"unplugTimeoutMs\":%d}",
             ssid, router_ip, request.ethernet_interface_name,
             request.wait_ethernet_unplug ? "true" : "false",
             request.unplug_timeout_ms);
    send_report(fd, "wifi", "running", 0, "Running Wi-Fi test", data);
    if (request.wait_ethernet_unplug && net_carrier_is_up(request.ethernet_interface_name)) {
        snprintf(data, sizeof(data),
                 "{\"ssid\":\"%s\",\"routerIp\":\"%s\",\"interfaceName\":\"%s\",\"waitEthernetUnplug\":true,"
                 "\"unplugTimeoutMs\":%d,\"ethernetLinkUp\":true,\"requiresCableUnplug\":true}",
                 ssid, router_ip, request.ethernet_interface_name, request.unplug_timeout_ms);
        send_report(fd, "wifi", "running", 0, "Please unplug Ethernet cable before Wi-Fi test", data);
    }
    if (wifi_nmcli_open(&device, NULL) != 0 ||
        wifi_nmcli_run_test(&device, &request, &result) != 0) {
        wifi_nmcli_close(&device);
        snprintf(data, sizeof(data),
                 "{\"ssid\":\"%s\",\"routerIp\":\"%s\",\"interfaceName\":\"%s\",\"wifiEnabled\":%s,\"connected\":%s,"
                 "\"ipAcquired\":%s,\"pingOk\":%s,\"ip\":\"%s\",\"activeSsid\":\"%s\",\"ethernetLinkUp\":%s,"
                 "\"requiresCableUnplug\":%s,\"failureReason\":\"%s\"}",
                 ssid, router_ip, device.interface_name,
                 result.wifi_enabled ? "true" : "false",
                 result.connected ? "true" : "false",
                 result.ip_acquired ? "true" : "false",
                 result.ping_ok ? "true" : "false",
                 result.ip, result.active_ssid,
                 result.ethernet_link_up ? "true" : "false",
                 result.requires_cable_unplug ? "true" : "false",
                 result.failure_reason);
        send_report(fd, "wifi", "failed",
                    result.error_code == 0 ? 4100 : result.error_code,
                    result.error_message[0] == '\0' ? "Wi-Fi test failed" : result.error_message, data);
        return -1;
    }
    wifi_nmcli_close(&device);
    snprintf(data, sizeof(data),
             "{\"ssid\":\"%s\",\"ip\":\"%s\",\"routerIp\":\"%s\",\"pingCount\":%d,\"avgDelayMs\":%d,\"interfaceName\":\"%s\"}",
             ssid, result.ip, router_ip,
             result.completed_ping_count, result.avg_delay_ms, device.interface_name);
    return send_report(fd, "wifi", "passed", 0, "Wi-Fi test passed", data);
}

static int run_ethernet(int fd, const char *test_start, const char *test_end)
{
    char interface_name[64] = "end0";
    char router_ip[64] = "192.168.110.1";
    struct ethernet_request request = {
        .interface_name = interface_name,
        .router_ip = router_ip,
        .ping_count = 4,
        .timeout_ms = 15000,
        .wait_cable_unplug = true,
        .unplug_timeout_ms = 10000,
    };
    struct ethernet_result result;
    char data[768];

    param_string(test_start, test_end, "interfaceName", interface_name, sizeof(interface_name));
    param_string(test_start, test_end, "routerIp", router_ip, sizeof(router_ip));
    request.ping_count = param_int(test_start, test_end, "pingCount", request.ping_count);
    request.timeout_ms = param_int(test_start, test_end, "timeoutMs", request.timeout_ms);
    request.wait_cable_unplug = param_bool(test_start, test_end, "waitCableUnplug", request.wait_cable_unplug);
    request.unplug_timeout_ms = param_int(test_start, test_end, "unplugTimeoutMs", request.unplug_timeout_ms);

    snprintf(data, sizeof(data),
             "{\"interfaceName\":\"%s\",\"routerIp\":\"%s\",\"wifiDisabled\":true}",
             interface_name, router_ip);
    send_report(fd, "ethernet", "running", 0, "Insert Ethernet cable", data);

    if (ethernet_nmcli_run_test(&request, &result) != 0) {
        snprintf(data, sizeof(data),
                 "{\"interfaceName\":\"%s\",\"routerIp\":\"%s\",\"wifiDisabled\":%s,\"linkUp\":%s,\"ipAcquired\":%s,\"pingOk\":%s,\"ip\":\"%s\"}",
                 result.interface_name, result.router_ip,
                 result.wifi_disabled ? "true" : "false",
                 result.link_up ? "true" : "false",
                 result.ip_acquired ? "true" : "false",
                 result.ping_ok ? "true" : "false",
                 result.ip);
        send_report(fd, "ethernet", "failed",
                    result.error_code == 0 ? 4800 : result.error_code,
                    result.message[0] == '\0' ? "Ethernet test failed" : result.message,
                    data);
        return -1;
    }

    snprintf(data, sizeof(data),
             "{\"interfaceName\":\"%s\",\"ip\":\"%s\",\"routerIp\":\"%s\",\"pingCount\":%d,\"avgDelayMs\":%d,\"pingOk\":true}",
             result.interface_name, result.ip, result.router_ip,
             result.completed_ping_count, result.avg_delay_ms);
    send_report(fd, "ethernet", "running", 0, "Remove Ethernet cable", data);
    if (request.wait_cable_unplug) {
        result.cable_unplugged = ethernet_nmcli_wait_cable_unplug(interface_name, request.unplug_timeout_ms) == 0;
    }

    snprintf(data, sizeof(data),
             "{\"interfaceName\":\"%s\",\"ip\":\"%s\",\"routerIp\":\"%s\",\"pingCount\":%d,\"avgDelayMs\":%d,\"wifiDisabled\":%s,\"cableUnplugged\":%s}",
             result.interface_name, result.ip, result.router_ip,
             result.completed_ping_count, result.avg_delay_ms,
             result.wifi_disabled ? "true" : "false",
             result.cable_unplugged ? "true" : "false");
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
    memset(&result, 0, sizeof(result));
    snprintf(data, sizeof(data),
             "{\"targetName\":\"%s\",\"minRssi\":%d,\"scanWindowMs\":%d}",
             target_name, request.min_rssi, request.timeout_ms);
    send_report(fd, "bluetooth", "running", 0, "Running Bluetooth scan", data);
    if (bluetoothctl_scan_target(&request, &result) != 0) {
        snprintf(data, sizeof(data),
                 "{\"targetName\":\"%s\",\"minRssi\":%d,\"scanWindowMs\":%d,\"found\":%s,"
                 "\"matchedName\":\"%s\",\"matchedMac\":\"%s\",\"matchedRssi\":%d,"
                 "\"bestSeenName\":\"%s\",\"bestSeenMac\":\"%s\",\"bestSeenRssi\":%d,"
                 "\"failureReason\":\"%s\"}",
                 target_name, request.min_rssi, request.timeout_ms,
                 result.found ? "true" : "false",
                 result.name, result.mac, result.matched_rssi,
                 result.best_seen_name, result.best_seen_mac, result.best_seen_rssi,
                 result.failure_reason);
        send_report(fd, "bluetooth", "failed",
                    result.error_code == 0 ? 4200 : result.error_code,
                    result.error_message[0] == '\0' ? "Bluetooth test failed" : result.error_message,
                    data);
        return -1;
    }
    snprintf(data, sizeof(data),
             "{\"targetName\":\"%s\",\"name\":\"%s\",\"mac\":\"%s\",\"rssi\":%d,\"minRssi\":%d,"
             "\"scanWindowMs\":%d,\"bestSeenName\":\"%s\",\"bestSeenMac\":\"%s\",\"bestSeenRssi\":%d}",
             target_name, result.name, result.mac, result.rssi, request.min_rssi,
             request.timeout_ms, result.best_seen_name, result.best_seen_mac, result.best_seen_rssi);
    return send_report(fd, "bluetooth", "passed", 0, "Bluetooth target found", data);
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
    char data[512];

    request.voltage_min_mv = param_int(test_start, test_end, "chargeVoltageMinMv", request.voltage_min_mv);
    request.voltage_max_mv = param_int(test_start, test_end, "chargeVoltageMaxMv", request.voltage_max_mv);
    request.current_min_ma = param_int(test_start, test_end, "chargeCurrentMinMa", request.current_min_ma);
    request.current_max_ma = param_int(test_start, test_end, "chargeCurrentMaxMa", request.current_max_ma);
    request.stable_sample_count = param_int(test_start, test_end, "stableSampleCount", request.stable_sample_count);
    request.sample_interval_ms = param_int(test_start, test_end, "sampleIntervalMs", request.sample_interval_ms);
    request.timeout_ms = param_int(test_start, test_end, "timeoutMs", request.timeout_ms);
    memset(&result, 0, sizeof(result));
    send_report(fd, "typec_fast_charge", "running", 0, "Reading fast charge values", "{}");
    if (fast_charge_open(&device) != 0 ||
        fast_charge_run_test(&device, &request, &result) != 0) {
        fast_charge_close(&device);
        snprintf(data, sizeof(data),
                 "{\"online\":%s,\"voltageMv\":%d,\"currentMa\":%d,\"voltageMinMv\":%d,\"voltageMaxMv\":%d,\"currentMinMa\":%d,\"currentMaxMa\":%d}",
                 result.charger_online ? "true" : "false", result.voltage_mv, result.current_ma,
                 request.voltage_min_mv, request.voltage_max_mv,
                 request.current_min_ma, request.current_max_ma);
        send_report(fd, "typec_fast_charge", "failed",
                    result.error_code == 0 ? 4400 : result.error_code,
                    result.message[0] == '\0' ? "Fast charge test failed" : result.message,
                    data);
        return -1;
    }
    fast_charge_close(&device);
    snprintf(data, sizeof(data),
             "{\"online\":%s,\"voltageMv\":%d,\"currentMa\":%d,\"stableSamples\":%d,\"voltageMinMv\":%d,\"voltageMaxMv\":%d,\"currentMinMa\":%d,\"currentMaxMa\":%d}",
             result.charger_online ? "true" : "false", result.voltage_mv, result.current_ma,
             result.stable_samples, request.voltage_min_mv, request.voltage_max_mv,
             request.current_min_ma, request.current_max_ma);
    return send_report(fd, "typec_fast_charge", "passed", 0, result.message, data);
}

static int elapsed_ms(const struct timespec *start, const struct timespec *now)
{
    return (int)((now->tv_sec - start->tv_sec) * 1000 +
                 (now->tv_nsec - start->tv_nsec) / 1000000);
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
    struct timespec start, now;
    uint32_t detected = 0;
    int timeout_ms = param_int(test_start, test_end, "timeoutMs", config->keys_timeout_ms);
    char data[256];

    if (timeout_ms < 30000) timeout_ms = 30000;
    snprintf(data, sizeof(data),
             "{\"expectedKeys\":[\"up\",\"down\",\"left\",\"right\",\"confirm\"],\"detectedMask\":0,\"expectedMask\":%u,\"timeoutMs\":%d}",
             expected, timeout_ms);
    send_report(fd, "keys", "running", 0,
                "Press Up, Down, Left, Right and Confirm within 30 seconds",
                data);
    if (key_input_open(&input) != 0) {
        send_report(fd, "keys", "failed", 4000, "Unable to open key input devices", "{}");
        return -1;
    }
    key_input_drain_pending(&input);

    clock_gettime(CLOCK_MONOTONIC, &start);
    while (detected != expected) {
        int remaining;
        int rc;
        clock_gettime(CLOCK_MONOTONIC, &now);
        remaining = timeout_ms - elapsed_ms(&start, &now);
        if (remaining <= 0) {
            key_input_close(&input);
            snprintf(data, sizeof(data), "{\"detectedMask\":%u,\"expectedMask\":%u}",
                     detected, expected);
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
            snprintf(data, sizeof(data),
                     "{\"key\":\"%s\",\"rawCode\":%d,\"detectedMask\":%u,\"expectedMask\":%u,\"timeoutMs\":%d}",
                     key_input_name(event.key), event.raw_code, detected, expected, timeout_ms);
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
    struct camera_stream_request request = {
        .device_path = device_path,
        .stream_frame_count = config->camera_stream_frame_count,
        .timeout_ms = 3000,
        .require_exposure_interrupt = config->camera_require_exposure_interrupt != 0,
        .exposure_counter_path = exposure_counter_path,
        .exposure_frame_count = config->camera_exposure_frame_count,
    };
    struct camera_stream_result result;
    char data[512];

    snprintf(device_path, sizeof(device_path), "%s", config->camera_device_path);
    exposure_counter_path[0] = '\0';
    if (config->camera_exposure_counter_path != NULL) {
        snprintf(exposure_counter_path, sizeof(exposure_counter_path), "%s", config->camera_exposure_counter_path);
    }
    param_string(test_start, test_end, "devicePath", device_path, sizeof(device_path));
    param_string(test_start, test_end, "exposureCounterPath", exposure_counter_path, sizeof(exposure_counter_path));
    request.stream_frame_count = param_int(test_start, test_end, "streamFrameCount", request.stream_frame_count);
    request.timeout_ms = param_int(test_start, test_end, "timeoutMs", request.timeout_ms);
    request.exposure_frame_count = param_int(test_start, test_end, "minInterruptCount", request.exposure_frame_count);
    request.exposure_frame_count = param_int(test_start, test_end, "exposureFrameCount", request.exposure_frame_count);
    request.require_exposure_interrupt = param_bool(test_start, test_end, "requireExposureInterrupt", request.require_exposure_interrupt);
    if (exposure_counter_path[0] != '\0') request.require_exposure_interrupt = 1;
    memset(&result, 0, sizeof(result));
    send_report(fd, "typec_camera", "running", 0, "Running camera stream test", "{}");
    if (camera_stream_run_test(&request, &result) != 0) {
        snprintf(data, sizeof(data),
                 "{\"device\":\"%s\",\"capturedFrames\":%d,\"exposureDelta\":%d,\"streamOk\":%s,\"exposureOk\":%s,\"requiredExposureFrames\":%d}",
                 result.device_path, result.captured_frames, result.exposure_delta,
                 result.stream_ok ? "true" : "false",
                 result.exposure_ok ? "true" : "false",
                 request.exposure_frame_count);
        send_report(fd, "typec_camera", "failed",
                    result.error_code == 0 ? 4700 : result.error_code,
                    result.message[0] == '\0' ? "Camera stream test failed" : result.message,
                    data);
        return -1;
    }
    snprintf(data, sizeof(data),
             "{\"device\":\"%s\",\"capturedFrames\":%d,\"exposureDelta\":%d,\"streamOk\":%s,\"exposureOk\":%s,\"requiredExposureFrames\":%d}",
             result.device_path, result.captured_frames, result.exposure_delta,
             result.stream_ok ? "true" : "false",
             result.exposure_ok ? "true" : "false",
             request.exposure_frame_count);
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

    if (executed == 0) {
        if (strstr(request_json, "\"tests\"") != NULL) {
            send_report(fd, "unknown", "failed", 3000, "No supported test id found", "{}");
            return send_completed(fd, session_id, "failed", 3000, "No supported test id found");
        }
        run_board_state(fd);
    }

    if (failed_count > 0) {
        char message[160];
        snprintf(message, sizeof(message), "Session completed with %d failed test(s), %d skipped, first failed: %s",
                 failed_count, skipped_count, first_failed_test);
        return send_completed(fd, session_id, "failed", first_failed_code, message);
    }
    if (skipped_count > 0) {
        char message[160];
        snprintf(message, sizeof(message), "Session completed with %d skipped test(s)", skipped_count);
        return send_completed(fd, session_id, "passed", 0, message);
    }
    return send_completed(fd, session_id, "passed", 0, "Session completed");
}
