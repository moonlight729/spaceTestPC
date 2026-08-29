#define _GNU_SOURCE
#include "wifi_nmcli.h"

#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/wait.h>
#include <unistd.h>

#define WIFI_CMD_OUTPUT 131072
#define WIFI_SCAN_BUSY_RETRY_INTERVAL_MS 1000
#define WIFI_RADIO_ENABLE_TIMEOUT_MS 5000
#define WIFI_RADIO_POLL_INTERVAL_MS 200
#define WIFI_SCAN_SETTLE_MS 1000
#define WIFI_MAX_RSSI_SAMPLES 12

static char *trim_left(char *text);

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
        setenv("LC_ALL", "C", 1);
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

static int output_contains_scan_busy(const char *output)
{
    if (output == NULL) return 0;
    return strstr(output, "Device or resource busy") != NULL ||
           strstr(output, "resource busy") != NULL ||
           strstr(output, "(-16)") != NULL ||
           strstr(output, "EBUSY") != NULL;
}

static void sleep_ms_wifi(int ms)
{
    if (ms <= 0) return;
    usleep((useconds_t)ms * 1000U);
}

static void trim_command_output(char *output)
{
    char *start;
    size_t length;

    if (output == NULL) return;
    start = trim_left(output);
    if (start != output) memmove(output, start, strlen(start) + 1);
    length = strlen(output);
    while (length > 0 &&
           (output[length - 1] == '\n' || output[length - 1] == '\r' ||
            output[length - 1] == ' ' || output[length - 1] == '\t')) {
        output[--length] = '\0';
    }
}

static int read_wifi_radio_enabled(int *enabled, char *output, size_t output_size)
{
    char *const argv[] = { "nmcli", "radio", "wifi", NULL };

    if (enabled == NULL || output == NULL || output_size == 0) return -1;
    output[0] = '\0';
    if (run_command(argv, output, output_size) != 0) return -1;
    trim_command_output(output);
    if (strcmp(output, "enabled") == 0) {
        *enabled = 1;
        return 0;
    }
    if (strcmp(output, "disabled") == 0) {
        *enabled = 0;
        return 0;
    }
    return -1;
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

static int parse_link_output(const char *output, const char *target_ssid, int *rssi)
{
    char buffer[4096];
    char ssid[256] = "";
    int signal = -127;
    int have_signal = 0;
    char *line;
    char *save;

    if (output == NULL || target_ssid == NULL || rssi == NULL) return 0;
    snprintf(buffer, sizeof(buffer), "%s", output);
    for (line = strtok_r(buffer, "\n", &save); line != NULL; line = strtok_r(NULL, "\n", &save)) {
        char *trimmed = trim_left(line);
        double signal_dbm;
        if (strncmp(trimmed, "SSID: ", 6) == 0) {
            snprintf(ssid, sizeof(ssid), "%s", trimmed + 6);
        } else if (sscanf(trimmed, "signal: %lf dBm", &signal_dbm) == 1) {
            signal = (int)(signal_dbm < 0 ? signal_dbm - 0.5 : signal_dbm + 0.5);
            have_signal = 1;
        }
    }
    if (strcmp(ssid, target_ssid) != 0 || !have_signal) return 0;
    *rssi = signal;
    return 1;
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
    char *link_status_argv[] = { "iw", "dev", device != NULL ? device->interface_name : NULL, "link", NULL };
    int radio_enabled = 0;
    int max_scan_attempts;
    int scan_interval_ms;
    int target_valid_samples;
    int elapsed_ms = 0;
    int attempt;

    if (device == NULL || request == NULL || result == NULL ||
        request->ssid == NULL || request->ssid[0] == '\0' || device->interface_name[0] == '\0') {
        errno = EINVAL;
        return -1;
    }

    memset(result, 0, sizeof(*result));
    result->rssi = -127;

    if (read_wifi_radio_enabled(&radio_enabled, output, sizeof(output)) != 0) {
        set_error(result, 4104, output[0] ? output : "failed to read Wi-Fi radio state", "wifi_radio_state_unknown");
        return -1;
    }

    if (!radio_enabled) {
        int elapsed_ms = 0;
        if (run_command(radio_argv, output, sizeof(output)) != 0) {
            set_error(result, 4104, output[0] ? output : "failed to enable Wi-Fi radio", "wifi_radio_off");
            return -1;
        }
        while (elapsed_ms < WIFI_RADIO_ENABLE_TIMEOUT_MS) {
            if (read_wifi_radio_enabled(&radio_enabled, output, sizeof(output)) == 0 && radio_enabled) break;
            sleep_ms_wifi(WIFI_RADIO_POLL_INTERVAL_MS);
            elapsed_ms += WIFI_RADIO_POLL_INTERVAL_MS;
        }
        if (!radio_enabled) {
            set_error(result, 4104, "Wi-Fi radio did not become enabled", "wifi_radio_enable_timeout");
            return -1;
        }
    }
    result->wifi_enabled = true;

    if (run_command(link_argv, output, sizeof(output)) != 0) {
        set_error(result, 4103, output[0] ? output : "failed to bring Wi-Fi interface up", "wifi_interface_down");
        return -1;
    }

    max_scan_attempts = request->max_scan_attempts > 0 ? request->max_scan_attempts : 8;
    if (max_scan_attempts > WIFI_MAX_RSSI_SAMPLES) max_scan_attempts = WIFI_MAX_RSSI_SAMPLES;
    scan_interval_ms = request->scan_interval_ms >= 0 ? request->scan_interval_ms : 1000;
    target_valid_samples = request->target_valid_samples > 0 ? request->target_valid_samples : 3;
    if (target_valid_samples > max_scan_attempts) target_valid_samples = max_scan_attempts;

    sleep_ms_wifi(WIFI_SCAN_SETTLE_MS);

    /* Reading the current association is much more reliable than starting an
       active scan on drivers that occasionally return an empty scan result.
       It still reports the real nl80211 dBm value. */
    for (attempt = 0; attempt < target_valid_samples; ++attempt) {
        int link_rssi;
        output[0] = '\0';
        if (run_command(link_status_argv, output, sizeof(output)) != 0 ||
            !parse_link_output(output, request->ssid, &link_rssi)) {
            break;
        }
        result->rssi_samples[result->valid_sample_count++] = link_rssi;
        result->used_link_rssi = true;
        if (result->valid_sample_count < target_valid_samples) sleep_ms_wifi(300);
    }
    if (result->valid_sample_count >= target_valid_samples) goto calculate_result;

    for (attempt = 1; attempt <= max_scan_attempts; ++attempt) {
        struct wifi_result sample;
        int scan_rc;
        int scan_timeout_ms = request->scan_timeout_ms > 0 ? request->scan_timeout_ms : 15000;

        memset(&sample, 0, sizeof(sample));
        sample.rssi = -127;
        output[0] = '\0';
        result->scan_attempt_count++;
        scan_rc = run_command(scan_argv, output, sizeof(output));
        if (scan_rc != 0) {
            if (strstr(output, "Operation not permitted") != NULL || strstr(output, "Permission denied") != NULL) {
                set_error(result, 4101, output, "scan_permission_denied");
                return -1;
            }
            if (output_contains_scan_busy(output)) result->scan_busy_count++;
            result->scan_retry_count++;
        } else {
            (void)parse_scan_output(output, request->ssid, &sample);
            if (sample.found) {
                result->rssi_samples[result->valid_sample_count++] = sample.rssi;
                if (result->valid_sample_count >= target_valid_samples) break;
            } else if (output[0] == '\0') {
                result->empty_scan_count++;
            }
        }

        if (attempt < max_scan_attempts && elapsed_ms + scan_interval_ms < scan_timeout_ms) {
            sleep_ms_wifi(scan_interval_ms);
            elapsed_ms += scan_interval_ms;
        } else if (attempt < max_scan_attempts) {
            break;
        }
    }

calculate_result:
    if (result->valid_sample_count > 0) {
        int i;
        int j;
        for (i = 0; i < result->valid_sample_count - 1; ++i) {
            for (j = i + 1; j < result->valid_sample_count; ++j) {
                if (result->rssi_samples[j] < result->rssi_samples[i]) {
                    int value = result->rssi_samples[i];
                    result->rssi_samples[i] = result->rssi_samples[j];
                    result->rssi_samples[j] = value;
                }
            }
        }
        result->found = true;
        result->rssi = result->rssi_samples[result->valid_sample_count / 2];
        return 0;
    }

    result->found = false;
    result->rssi = -127;
    set_error(result, 4100, "Target SSID not found after repeated scans", "ssid_not_found");
    return 0;
}
