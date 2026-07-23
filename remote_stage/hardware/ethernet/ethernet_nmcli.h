#ifndef SPACETEST3576_ETHERNET_NMCLI_H
#define SPACETEST3576_ETHERNET_NMCLI_H

#include <stdbool.h>

struct ethernet_request {
    const char *interface_name;
    const char *router_ip;
    int ping_count;
    int timeout_ms;
};

struct ethernet_result {
    char interface_name[64];
    char ip[64];
    char router_ip[64];
    char failure_reason[64];
    int completed_ping_count;
    int avg_delay_ms;
    bool wifi_disabled;
    bool link_up;
    bool ip_acquired;
    bool ping_ok;
    int error_code;
    char message[160];
};

int ethernet_nmcli_run_test(const struct ethernet_request *request,
                            struct ethernet_result *result);

#endif
