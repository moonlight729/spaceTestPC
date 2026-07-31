#ifndef SPACETEST3576_BLUETOOTHCTL_SCAN_H
#define SPACETEST3576_BLUETOOTHCTL_SCAN_H

#include <stdbool.h>

struct bluetooth_request {
    const char *target_name;
    int timeout_ms;
    int min_rssi;
};

struct bluetooth_result {
    bool found;
    char name[128];
    char mac[32];
    int rssi;
    char best_seen_name[128];
    char best_seen_mac[32];
    int best_seen_rssi;
    int matched_rssi;
    int error_code;
    char failure_reason[64];
    char error_message[256];
};

int bluetoothctl_health(void);
int bluetoothctl_scan_target(const struct bluetooth_request *request,
                             struct bluetooth_result *result);

#endif
