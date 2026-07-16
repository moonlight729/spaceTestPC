#define _GNU_SOURCE
#include "wifi_nmcli.h"

#include <ctype.h>
#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/wait.h>
#include <unistd.h>

#define WIFI_CMD_OUTPUT 4096

static int run_command(char *const argv[], char *output, size_t output_size)
{
    int pipefd[2], status;
    pid_t pid;
    ssize_t read_count;
    size_t used = 0;

    if (pipe(pipefd) != 0) return -1;
    pid = fork();
    if (pid < 0) { close(pipefd[0]); close(pipefd[1]); return -1; }
    if (pid == 0) {
        dup2(pipefd[1], STDOUT_FILENO);
        dup2(pipefd[1], STDERR_FILENO);
        close(pipefd[0]); close(pipefd[1]);
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

static void set_error(struct wifi_result *result, int code, const char *message)
{
    size_t length;
    result->error_code = code;
    length = strnlen(message, sizeof(result->error_message) - 1);
    memcpy(result->error_message, message, length);
    result->error_message[length] = '\0';
}

int wifi_nmcli_open(struct wifi_device *device, const char *interface_name)
{
    char output[WIFI_CMD_OUTPUT], *line, *save;
    char *const argv[] = { "nmcli", "-t", "-f", "DEVICE,TYPE", "device", "status", NULL };
    if (device == NULL) { errno = EINVAL; return -1; }
    memset(device, 0, sizeof(*device));
    if (interface_name != NULL) {
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

int wifi_nmcli_connect(struct wifi_device *device, const struct wifi_request *request,
                       struct wifi_result *result)
{
    char output[WIFI_CMD_OUTPUT], wait_seconds[16];
    char *radio_argv[] = { "nmcli", "radio", "wifi", "on", NULL };
    char *argv[] = { "nmcli", "--wait", wait_seconds, "device", "wifi", "connect",
                     (char *)request->ssid, "password", (char *)request->password,
                     "ifname", device->interface_name, NULL };
    int rc;
    if (device == NULL || request == NULL || result == NULL || request->ssid == NULL ||
        device->interface_name[0] == '\0') { errno = EINVAL; return -1; }
    if (run_command(radio_argv, output, sizeof(output)) != 0) {
        set_error(result, 4104, output[0] ? output : "failed to enable Wi-Fi radio");
        return -1;
    }
    if (request->reuse_current_connection) {
        char active_ssid[256];
        if (wifi_nmcli_get_active_ssid(device, active_ssid, sizeof(active_ssid)) != 0 ||
            strcmp(active_ssid, request->ssid) != 0) {
            set_error(result, 4100, "requested SSID is not the active connection");
            return -1;
        }
        result->connected = true;
        return 0;
    }
    if (request->password == NULL) { errno = EINVAL; return -1; }
    snprintf(wait_seconds, sizeof(wait_seconds), "%d", (request->timeout_ms + 999) / 1000);
    rc = run_command(argv, output, sizeof(output));
    if (rc != 0) { set_error(result, 4101, output[0] ? output : "nmcli connection failed"); return -1; }
    result->connected = true;
    return 0;
}

int wifi_nmcli_get_active_ssid(const struct wifi_device *device, char *ssid, size_t ssid_size)
{
    char output[WIFI_CMD_OUTPUT];
    char *argv[] = { "nmcli", "-t", "-f", "ACTIVE,SSID", "device", "wifi", "list",
                     "ifname", (char *)device->interface_name, NULL };
    char *line, *save;
    if (device == NULL || ssid == NULL || ssid_size == 0) { errno = EINVAL; return -1; }
    if (run_command(argv, output, sizeof(output)) != 0) return -1;
    for (line = strtok_r(output, "\n", &save); line != NULL; line = strtok_r(NULL, "\n", &save)) {
        if (strncmp(line, "yes:", 4) == 0) {
            snprintf(ssid, ssid_size, "%s", line + 4);
            return ssid[0] == '\0' ? -1 : 0;
        }
    }
    return -1;
}

int wifi_nmcli_get_ipv4(const struct wifi_device *device, char *ip, size_t ip_size)
{
    char output[WIFI_CMD_OUTPUT], *slash;
    char *argv[] = { "nmcli", "-g", "IP4.ADDRESS", "device", "show", (char *)device->interface_name, NULL };
    if (device == NULL || ip == NULL || ip_size == 0) { errno = EINVAL; return -1; }
    if (run_command(argv, output, sizeof(output)) != 0 || output[0] == '\0') return -1;
    output[strcspn(output, "\r\n")] = '\0';
    slash = strchr(output, '/'); if (slash != NULL) *slash = '\0';
    if (output[0] == '\0') return -1;
    snprintf(ip, ip_size, "%s", output);
    return 0;
}

int wifi_nmcli_ping_gateway(const struct wifi_device *device, const struct wifi_request *request,
                            struct wifi_result *result)
{
    char output[WIFI_CMD_OUTPUT], count[16], timeout[16], *avg;
    char *argv[] = { "ping", "-I", (char *)device->interface_name, "-n", "-c", count, "-W", timeout, (char *)request->router_ip, NULL };
    int rc;
    if (device == NULL || request == NULL || result == NULL || request->router_ip == NULL ||
        device->interface_name[0] == '\0') { errno = EINVAL; return -1; }
    snprintf(count, sizeof(count), "%d", request->ping_count);
    snprintf(timeout, sizeof(timeout), "%d", (request->timeout_ms + 999) / 1000);
    rc = run_command(argv, output, sizeof(output));
    result->completed_ping_count = rc == 0 ? request->ping_count : 0;
    if (rc != 0) { set_error(result, 4103, output[0] ? output : "gateway ping failed"); return -1; }
    avg = strstr(output, " = ");
    if (avg != NULL) {
        double minimum, average;
        if (sscanf(avg + 3, "%lf/%lf", &minimum, &average) == 2) result->avg_delay_ms = (int)(average + 0.5);
    }
    result->ping_ok = true;
    return 0;
}

int wifi_nmcli_run_test(struct wifi_device *device, const struct wifi_request *request,
                        struct wifi_result *result)
{
    if (device == NULL || request == NULL || result == NULL) { errno = EINVAL; return -1; }
    memset(result, 0, sizeof(*result));
    if (wifi_nmcli_connect(device, request, result) != 0) return -1;
    if (wifi_nmcli_get_ipv4(device, result->ip, sizeof(result->ip)) != 0) {
        set_error(result, 4102, "DHCP did not provide an IPv4 address");
        return -1;
    }
    return wifi_nmcli_ping_gateway(device, request, result);
}
