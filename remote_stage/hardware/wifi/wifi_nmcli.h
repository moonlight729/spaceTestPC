#ifndef SPACETEST3576_WIFI_NMCLI_H
#define SPACETEST3576_WIFI_NMCLI_H

#include <stdbool.h>

struct wifi_device {
    char interface_name[32];
};

struct wifi_request {
    const char *ssid;
    int scan_timeout_ms;
};

struct wifi_result {
    bool wifi_enabled;
    bool found;
    int rssi;
    int error_code;
    char failure_reason[64];
    char error_message[256];
};

/* If interface_name is NULL, the first nmcli Wi-Fi interface is selected. */
int wifi_nmcli_open(struct wifi_device *device, const char *interface_name);
void wifi_nmcli_close(struct wifi_device *device);
int wifi_nmcli_scan_signal(struct wifi_device *device, const struct wifi_request *request,
                           struct wifi_result *result);

#endif
