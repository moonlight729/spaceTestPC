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
    char password_buffer[128];
    int ping_count;
    int timeout_ms;
    bool reuse_current_connection;
    const char *ethernet_interface_name;
    char ethernet_interface_name_buffer[32];
    bool wait_ethernet_unplug;
    int unplug_timeout_ms;
};

struct wifi_result {
    bool wifi_enabled;
    bool connected;
    bool ip_acquired;
    bool ping_ok;
    bool ethernet_link_up;
    bool requires_cable_unplug;
    char ip[64];
    char active_ssid[128];
    int avg_delay_ms;
    int completed_ping_count;
    int error_code;
    char failure_reason[64];
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
