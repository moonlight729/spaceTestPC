#define _GNU_SOURCE
#include "wifi_nmcli.h"

#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/wait.h>
#include <unistd.h>

#define WIFI_CMD_OUTPUT 32768

static int run_command(char *const argv[], char *output, size_t output_size)
{
    int pipefd[2];
    int status;
    pid_t pid;
    ssize_t read_count;
    size_t used = 0;

    if (pipe(pipefd) != 0) return -1;
    pid = fork();
    if (pid < 0) {
        close(pipefd[0]);
        close(pipefd[1]);
        return -1;
    }
    if (pid == 0) {
        dup2(pipefd[1], STDOUT_FILENO);
        dup2(pipefd[1], STDERR_FILENO);
        close(pipefd[0]);
        close(pipefd[1]);
        execvp(argv[0], argv);
        _exit(127);
    }

    close(pipefd[1]);
    while (used + 1 < output_size &&
           (read_count = read(pipefd[0], output + used, output_size - used - 1)) > 0) {
        used += (size_t)read_count;
    }
    output[used] = '\0';
    close(pipefd[0]);
    if (waitpid(pid, &status, 0) < 0) return -1;
    return WIFEXITED(status) ? WEXITSTATUS(status) : -1;
}

static void set_error(struct wifi_result *result, int code, const char *message, const char *reason)
{
    size_t message_length;
    size_t reason_length;

    if (result == NULL) return;
    result->error_code = code;

    message_length = strnlen(message, sizeof(result->error_message) - 1);
    memcpy(result->error_message, message, message_length);
    result->error_message[message_length] = '\0';

    reason_length = strnlen(reason, sizeof(result->failure_reason) - 1);
    memcpy(result->failure_reason, reason, reason_length);
    result->failure_reason[reason_length] = '\0';
}

static char *trim_left(char *text)
{
    while (*text == ' ' || *text == '\t' || *text == '\r') ++text;
    return text;
}

static void finalize_scan_block(const char *target_ssid,
                                const char *current_ssid,
                                int current_signal_valid,
                                int current_signal,
                                int *ssid_seen,
                                int *best_rssi,
                                int *matched)
{
    if (target_ssid == NULL || current_ssid == NULL || strcmp(current_ssid, target_ssid) != 0) {
        return;
    }

    *ssid_seen = 1;
    if (!current_signal_valid) return;
    if (!*matched || current_signal > *best_rssi) {
        *best_rssi = current_signal;
    }
    *matched = 1;
}

static int parse_scan_output(const char *scan_output, const char *target_ssid, struct wifi_result *result)
{
    char buffer[WIFI_CMD_OUTPUT];
    char current_ssid[256] = "";
    int current_signal = -127;
    int current_signal_valid = 0;
    int best_rssi = -127;
    int matched = 0;
    int ssid_seen = 0;
    char *line;
    char *save;

    if (scan_output == NULL || target_ssid == NULL || result == NULL) return -1;
    snprintf(buffer, sizeof(buffer), "%s", scan_output);

    for (line = strtok_r(buffer, "\n", &save); line != NULL; line = strtok_r(NULL, "\n", &save)) {
        char *trimmed = trim_left(line);
        double signal_dbm;

        if (strncmp(trimmed, "BSS ", 4) == 0) {
            finalize_scan_block(target_ssid, current_ssid, current_signal_valid, current_signal,
                                &ssid_seen, &best_rssi, &matched);
            current_ssid[0] = '\0';
            current_signal = -127;
            current_signal_valid = 0;
            continue;
        }

        if (strncmp(trimmed, "SSID: ", 6) == 0) {
            snprintf(current_ssid, sizeof(current_ssid), "%s", trimmed + 6);
            continue;
        }

        if (sscanf(trimmed, "signal: %lf dBm", &signal_dbm) == 1) {
            current_signal = (int)(signal_dbm < 0 ? signal_dbm - 0.5 : signal_dbm + 0.5);
            current_signal_valid = 1;
        }
    }

    finalize_scan_block(target_ssid, current_ssid, current_signal_valid, current_signal,
                        &ssid_seen, &best_rssi, &matched);

    if (!ssid_seen) {
        result->found = false;
        result->rssi = -127;
        set_error(result, 4100, "Target SSID not found", "ssid_not_found");
        return 0;
    }

    if (!matched) {
        result->found = false;
        result->rssi = -127;
        set_error(result, 4102, "Target SSID found but signal was unavailable", "signal_not_found");
        return 0;
    }

    result->found = true;
    result->rssi = best_rssi;
    return 0;
}

int wifi_nmcli_open(struct wifi_device *device, const char *interface_name)
{
    char output[4096];
    char *line;
    char *save;
    char *const argv[] = { "nmcli", "-t", "-f", "DEVICE,TYPE", "device", "status", NULL };

    if (device == NULL) {
        errno = EINVAL;
        return -1;
    }

    memset(device, 0, sizeof(*device));
    if (interface_name != NULL && interface_name[0] != '\0') {
        snprintf(device->interface_name, sizeof(device->interface_name), "%s", interface_name);
        return 0;
    }

    if (run_command(argv, output, sizeof(output)) != 0) return -1;
    for (line = strtok_r(output, "\n", &save); line != NULL; line = strtok_r(NULL, "\n", &save)) {
        char *separator = strrchr(line, ':');
        if (separator != NULL && strcmp(separator + 1, "wifi") == 0) {
            *separator = '\0';
            snprintf(device->interface_name, sizeof(device->interface_name), "%s", line);
            return 0;
        }
    }

    errno = ENODEV;
    return -1;
}

void wifi_nmcli_close(struct wifi_device *device)
{
    if (device != NULL) memset(device, 0, sizeof(*device));
}

int wifi_nmcli_scan_signal(struct wifi_device *device, const struct wifi_request *request,
                           struct wifi_result *result)
{
    char output[WIFI_CMD_OUTPUT];
    char *radio_argv[] = { "nmcli", "radio", "wifi", "on", NULL };
    char *link_argv[] = { "ip", "link", "set", "dev", device != NULL ? device->interface_name : NULL, "up", NULL };
    char *scan_argv[] = { "iw", "dev", device != NULL ? device->interface_name : NULL, "scan", NULL };

    if (device == NULL || request == NULL || result == NULL ||
        request->ssid == NULL || request->ssid[0] == '\0' || device->interface_name[0] == '\0') {
        errno = EINVAL;
        return -1;
    }

    memset(result, 0, sizeof(*result));
    result->rssi = -127;

    if (run_command(radio_argv, output, sizeof(output)) != 0) {
        set_error(result, 4104, output[0] ? output : "failed to enable Wi-Fi radio", "wifi_radio_off");
        return -1;
    }
    result->wifi_enabled = true;

    if (run_command(link_argv, output, sizeof(output)) != 0) {
        set_error(result, 4103, output[0] ? output : "failed to bring Wi-Fi interface up", "wifi_interface_down");
        return -1;
    }

    if (run_command(scan_argv, output, sizeof(output)) != 0) {
        set_error(result, 4101, output[0] ? output : "iw scan command failed", "scan_command_failed");
        return -1;
    }

    return parse_scan_output(output, request->ssid, result);
}
