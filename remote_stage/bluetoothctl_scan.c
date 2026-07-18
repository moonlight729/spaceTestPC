#define _GNU_SOURCE
#include "bluetoothctl_scan.h"

#include <errno.h>
#include <limits.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <strings.h>
#include <unistd.h>
#include <sys/wait.h>

#define LINE_SIZE 512

static void set_error(struct bluetooth_result *result, int code, const char *message, const char *reason)
{
    size_t length = strnlen(message, sizeof(result->error_message) - 1);
    size_t reason_length = strnlen(reason, sizeof(result->failure_reason) - 1);
    result->error_code = code;
    memcpy(result->error_message, message, length);
    result->error_message[length] = '\0';
    memcpy(result->failure_reason, reason, reason_length);
    result->failure_reason[reason_length] = '\0';
}

static int command_exit_status(const char *command)
{
    int status = system(command);
    return status >= 0 && WIFEXITED(status) ? WEXITSTATUS(status) : -1;
}

int bluetoothctl_health(void)
{
    /*
     * 最终扫描链路改成：
     * 1. 用 btmon 读取原始 HCI 广播事件，拿到可靠的 RSSI。
     * 2. 用 bluetoothctl 仅负责触发 BlueZ 开始扫描。
     * 因此这里需要同时确认两者都可用。
     */
    if (command_exit_status("bluetoothctl show >/dev/null 2>&1") != 0) return -1;
    if (command_exit_status("command -v btmon >/dev/null 2>&1") != 0) return -1;
    return 0;
}

static void update_best_seen(struct bluetooth_result *result, const char *name, const char *mac, int rssi)
{
    if (name[0] == '\0' || mac[0] == '\0') return;
    if (result->best_seen_name[0] == '\0' || rssi > result->best_seen_rssi) {
        snprintf(result->best_seen_name, sizeof(result->best_seen_name), "%s", name);
        snprintf(result->best_seen_mac, sizeof(result->best_seen_mac), "%s", mac);
        result->best_seen_rssi = rssi;
    }
}

static bool parse_btmon_address_line(const char *line, char *mac, size_t mac_size)
{
    const char *address = strstr(line, "Address: ");
    if (address == NULL) address = strstr(line, "LE Address: ");
    (void)mac_size;
    if (address == NULL) return false;
    address = strchr(address, ':');
    if (address == NULL) return false;
    ++address;
    while (*address == ' ') ++address;
    return sscanf(address, "%31s", mac) == 1;
}

static bool parse_btmon_name_line(const char *line, char *name, size_t name_size)
{
    const char *name_start = strstr(line, "Name (complete): ");
    if (name_start == NULL) name_start = strstr(line, "Name (short): ");
    if (name_start == NULL) return false;
    name_start = strchr(name_start, ':');
    if (name_start == NULL) return false;
    ++name_start;
    while (*name_start == ' ') ++name_start;
    snprintf(name, name_size, "%s", name_start);
    name[strcspn(name, "\r\n")] = '\0';
    return name[0] != '\0';
}

static void commit_btmon_report(const struct bluetooth_request *request,
                                struct bluetooth_result *result,
                                const char *report_mac,
                                const char *report_name,
                                bool has_rssi,
                                int report_rssi)
{
    bool is_target = false;
    if (report_mac[0] == '\0' || !has_rssi) return;
    if (report_name[0] != '\0') {
        update_best_seen(result, report_name, report_mac, report_rssi);
        if (strcmp(report_name, request->target_name) == 0) {
            snprintf(result->name, sizeof(result->name), "%s", report_name);
            snprintf(result->mac, sizeof(result->mac), "%s", report_mac);
            is_target = true;
        }
    }
    if (!is_target &&
        result->mac[0] != '\0' &&
        strcmp(report_mac, result->mac) == 0) {
        is_target = true;
    }
    if (is_target) {
        result->matched_rssi = report_rssi;
        result->rssi = report_rssi;
        if (result->name[0] != '\0') {
            update_best_seen(result, result->name, result->mac, report_rssi);
        }
        if (report_rssi >= request->min_rssi) result->found = true;
    }
}

static int capture_btmon_scan(const struct bluetooth_request *request,
                              struct bluetooth_result *result)
{
    FILE *stream;
    char log_path[128];
    char command[1024];
    char line[LINE_SIZE];
    char report_mac[32] = "";
    char report_name[128] = "";
    bool has_rssi = false;
    int report_rssi = -127;
    int seconds;
    int status;

    if (snprintf(log_path, sizeof(log_path), "/tmp/spacetest_btmon_%ld.log", (long)getpid()) >= (int)sizeof(log_path)) {
        set_error(result, 4201, "btmon log path is too long", "bluetooth_hci_error");
        return -1;
    }
    seconds = (request->timeout_ms + 999) / 1000;
    /*
     * 这块板子上的 bluetoothctl 扫描输出经常只有设备名，没有目标设备的
     * RSSI 行，因此不能再把 bluetoothctl 当成最终判定来源。
     * 这里改成 btmon 抓 HCI 原始广播，bluetoothctl 只负责触发扫描，
     * 这样既能保留现有 BlueZ 扫描流程，又能拿到真实 RSSI。
     */
    if (snprintf(command, sizeof(command),
                 "rm -f %s; "
                 "(timeout %d btmon > %s 2>&1) & "
                 "sleep 1; "
                 "timeout %d bluetoothctl --timeout %d scan on >/dev/null 2>&1; "
                 "wait; "
                 "cat %s",
                 log_path, seconds + 2, log_path, seconds, seconds, log_path) >= (int)sizeof(command)) {
        set_error(result, 4201, "btmon command is too long", "bluetooth_hci_error");
        return -1;
    }
    stream = popen(command, "r");
    if (stream == NULL) {
        set_error(result, 4201, "Unable to start btmon scan", "bluetooth_hci_error");
        return -1;
    }
    while (fgets(line, sizeof(line), stream) != NULL) {
        char parsed_mac[32] = "";
        char parsed_name[128] = "";
        int parsed_rssi;
        if (parse_btmon_address_line(line, parsed_mac, sizeof(parsed_mac))) {
            if (report_mac[0] != '\0' && strcmp(parsed_mac, report_mac) != 0) {
                commit_btmon_report(request, result, report_mac, report_name, has_rssi, report_rssi);
                report_name[0] = '\0';
                has_rssi = false;
                report_rssi = -127;
            }
            snprintf(report_mac, sizeof(report_mac), "%s", parsed_mac);
            continue;
        }
        if (parse_btmon_name_line(line, parsed_name, sizeof(parsed_name))) {
            snprintf(report_name, sizeof(report_name), "%s", parsed_name);
            continue;
        }
        if (strstr(line, "RSSI:") != NULL && sscanf(line, "%*[^:]: %d dBm", &parsed_rssi) == 1) {
            has_rssi = true;
            report_rssi = parsed_rssi;
            continue;
        }
        if (strncmp(line, "> HCI Event:", 12) == 0 || strncmp(line, "@ RAW Close:", 11) == 0) {
            commit_btmon_report(request, result, report_mac, report_name, has_rssi, report_rssi);
            report_mac[0] = '\0';
            report_name[0] = '\0';
            has_rssi = false;
            report_rssi = -127;
        }
    }
    commit_btmon_report(request, result, report_mac, report_name, has_rssi, report_rssi);
    status = pclose(stream);
    if (snprintf(command, sizeof(command), "rm -f %s >/dev/null 2>&1", log_path) < (int)sizeof(command)) {
        int cleanup_status = system(command);
        if (cleanup_status == -1) {
            /* 清理临时日志失败不影响最终测试结果。 */
        }
    }
    if (result->found) return 0;
    if (status == -1 || !WIFEXITED(status) || WEXITSTATUS(status) != 0) {
        set_error(result, 4202, "btmon scan failed", "bluetooth_hci_error");
    } else if (result->mac[0] != '\0') {
        set_error(result, 4203, "Target found but RSSI is below threshold", "target_found_but_rssi_low");
    } else {
        set_error(result, 4204, "Target Bluetooth name was not found", "target_not_found");
    }
    return -1;
}

int bluetoothctl_scan_target(const struct bluetooth_request *request,
                             struct bluetooth_result *result)
{
    if (request == NULL || result == NULL || request->target_name == NULL ||
        request->timeout_ms <= 0) { errno = EINVAL; return -1; }
    memset(result, 0, sizeof(*result));
    result->rssi = -127;
    result->matched_rssi = -127;
    result->best_seen_rssi = -127;
    if (bluetoothctl_health() != 0) {
        set_error(result, 4200, "Bluetooth controller is unavailable", "bluetooth_hci_error");
        return -1;
    }
    return capture_btmon_scan(request, result);
}
