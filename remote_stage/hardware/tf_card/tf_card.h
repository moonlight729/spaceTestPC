#ifndef SPACETEST3576_TF_CARD_H
#define SPACETEST3576_TF_CARD_H

#include <stdbool.h>
#include <stdint.h>

struct tf_card_request {
    const char *device_path;
    const char *mount_point;
    bool allow_format_ext4;
    int min_capacity_mb;
};

struct tf_card_result {
    char device_path[128];
    char filesystem[32];
    char mount_point[160];
    bool present;
    bool mounted;
    bool formatted;
    bool rw_passed;
    uint64_t total_mb;
    uint64_t free_mb;
    int error_code;
    char message[192];
};

int tf_card_run_test(const struct tf_card_request *request,
                     struct tf_card_result *result);

#endif
