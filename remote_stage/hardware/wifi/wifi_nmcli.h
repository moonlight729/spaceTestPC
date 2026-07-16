#ifndef SPACETEST3576_WIFI_NMCLI_H
#define SPACETEST3576_WIFI_NMCLI_H

#include <stdbool.h>
#include <stddef.h>

struct wifi_device {
    char interface_name[32];
};

struct wifi_request {
    const char *ssid;
    const char *password;
    const char *router_ip;
    int ping_count;
    int timeout_ms;
    bool reuse_current_connection;
};

struct wifi_result {
    bool connected;
    bool ping_ok;
    char ip[64];
    int avg_delay_ms;
    int completed_ping_count;
    int error_code;
    char error_message[256];
};

/* If interface_name is NULL, the first nmcli Wi-Fi interface is selected. */
int wifi_nmcli_open(struct wifi_device *device, const char *interface_name);
void wifi_nmcli_close(struct wifi_device *device);
int wifi_nmcli_connect(struct wifi_device *device, const struct wifi_request *request,
                       struct wifi_result *result);
int wifi_nmcli_get_active_ssid(const struct wifi_device *device, char *ssid, size_t ssid_size);
int wifi_nmcli_get_ipv4(const struct wifi_device *device, char *ip, size_t ip_size);
int wifi_nmcli_ping_gateway(const struct wifi_device *device, const struct wifi_request *request,
                            struct wifi_result *result);
int wifi_nmcli_run_test(struct wifi_device *device, const struct wifi_request *request,
                        struct wifi_result *result);

#endif
