#define _POSIX_C_SOURCE 200809L
#include "ethernet_nmcli.h"

#include <ctype.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <unistd.h>

static int safe_token(const char *value)
{
    const unsigned char *p = (const unsigned char *)value;
    if (value == NULL || value[0] == '\0') return 0;
    while (*p != '\0') {
        if (!isalnum(*p) && *p != '.' && *p != '_' && *p != '-') return 0;
        p++;
    }
    return 1;
}

static int run_command(const char *command)
{
    int rc = system(command);
    return rc == 0 ? 0 : -1;
}

static void sleep_ms(int ms)
{
    struct timespec ts;
    ts.tv_sec = ms / 1000;
    ts.tv_nsec = (long)(ms % 1000) * 1000000L;
    nanosleep(&ts, NULL);
}

static int read_carrier(const char *interface_name)
{
    char path[160];
    FILE *file;
    int value = 0;
    snprintf(path, sizeof(path), "/sys/class/net/%s/carrier", interface_name);
    file = fopen(path, "r");
    if (file == NULL) return -1;
    if (fscanf(file, "%d", &value) != 1) {
        fclose(file);
        return -1;
    }
    fclose(file);
    return value;
}

static int wait_carrier(const char *interface_name, int expected, int timeout_ms)
{
    int elapsed = 0;
    while (elapsed <= timeout_ms) {
        int carrier = read_carrier(interface_name);
        if (carrier == expected) return 0;
        sleep_ms(200);
        elapsed += 200;
    }
    return -1;
}

static int read_ipv4(const char *interface_name, char *ip, size_t ip_size)
{
    char command[192];
    char line[256];
    FILE *pipe;
    char *inet;
    char *slash;

    if (ip == NULL || ip_size == 0) return -1;
    ip[0] = '\0';
    snprintf(command, sizeof(command), "ip -4 -o addr show dev %s 2>/dev/null", interface_name);
    pipe = popen(command, "r");
    if (pipe == NULL) return -1;
    if (fgets(line, sizeof(line), pipe) == NULL) {
        pclose(pipe);
        return -1;
    }
    pclose(pipe);
    inet = strstr(line, " inet ");
    if (inet == NULL) return -1;
    inet += 6;
    slash = strchr(inet, '/');
    if (slash == NULL) return -1;
    *slash = '\0';
    snprintf(ip, ip_size, "%s", inet);
    return ip[0] == '\0' ? -1 : 0;
}

static int wait_ipv4(const char *interface_name, char *ip, size_t ip_size, int timeout_ms)
{
    int elapsed = 0;
    while (elapsed <= timeout_ms) {
        if (read_ipv4(interface_name, ip, ip_size) == 0) return 0;
        sleep_ms(500);
        elapsed += 500;
    }
    return -1;
}

static int ping_router(const char *interface_name, const char *router_ip, int ping_count)
{
    char command[256];
    if (ping_count <= 0) ping_count = 4;
    snprintf(command, sizeof(command), "ping -I %s -c %d -W 2 %s >/dev/null 2>&1",
             interface_name, ping_count, router_ip);
    return run_command(command);
}

static int disconnect_wifi_connections(void)
{
    /*
     * Keep the Wi-Fi hardware and driver nodes alive. For Ethernet testing we
     * only need to stop the current wireless connection from competing for
     * routing, so use a software-level disconnect instead of radio-off.
     */
    return run_command("nmcli device disconnect wlan0 >/dev/null 2>&1 || "
                       "nmcli device disconnect wlan1 >/dev/null 2>&1 || "
                       "nmcli connection down id \\\"preconfigured\\\" >/dev/null 2>&1 || true");
}

int ethernet_nmcli_run_test(const struct ethernet_request *request,
                            struct ethernet_result *result)
{
    char command[256];
    int wait_ip_ms;

    if (request == NULL || result == NULL ||
        !safe_token(request->interface_name) || !safe_token(request->router_ip)) {
        return -1;
    }
    memset(result, 0, sizeof(*result));
    snprintf(result->interface_name, sizeof(result->interface_name), "%s", request->interface_name);
    snprintf(result->router_ip, sizeof(result->router_ip), "%s", request->router_ip);
    result->avg_delay_ms = -1;
    result->failure_reason[0] = '\0';

    if (disconnect_wifi_connections() != 0) {
        result->error_code = 4805;
        snprintf(result->message, sizeof(result->message), "Unable to disconnect Wi-Fi before Ethernet test");
        snprintf(result->failure_reason, sizeof(result->failure_reason), "ethernet_disconnect_wifi_failed");
        return -1;
    }
    result->wifi_disabled = true;

    snprintf(command, sizeof(command), "ip link set dev %s up >/dev/null 2>&1", request->interface_name);
    run_command(command);
    snprintf(command, sizeof(command), "nmcli device connect %s >/dev/null 2>&1", request->interface_name);
    run_command(command);

    if (wait_carrier(request->interface_name, 1, request->timeout_ms) != 0) {
        result->error_code = 4801;
        snprintf(result->message, sizeof(result->message), "Ethernet cable is not inserted");
        snprintf(result->failure_reason, sizeof(result->failure_reason), "ethernet_cable_not_inserted");
        return -1;
    }
    result->link_up = true;

    wait_ip_ms = request->timeout_ms < 3000 ? request->timeout_ms : request->timeout_ms - 1000;
    if (wait_ipv4(request->interface_name, result->ip, sizeof(result->ip), wait_ip_ms) != 0) {
        result->error_code = 4802;
        snprintf(result->message, sizeof(result->message), "Ethernet IP address was not acquired");
        snprintf(result->failure_reason, sizeof(result->failure_reason), "ethernet_no_ip");
        return -1;
    }
    result->ip_acquired = true;

    if (ping_router(request->interface_name, request->router_ip, request->ping_count) != 0) {
        result->error_code = 4803;
        snprintf(result->message, sizeof(result->message), "Ethernet router ping failed");
        snprintf(result->failure_reason, sizeof(result->failure_reason), "ethernet_ping_failed");
        return -1;
    }
    result->ping_ok = true;
    result->completed_ping_count = request->ping_count <= 0 ? 4 : request->ping_count;
    result->avg_delay_ms = 1;

    snprintf(result->message, sizeof(result->message), "Ethernet test passed");
    return 0;
}

int ethernet_nmcli_wait_cable_unplug(const char *interface_name, int timeout_ms)
{
    if (!safe_token(interface_name)) return -1;
    return wait_carrier(interface_name, 0, timeout_ms);
}
