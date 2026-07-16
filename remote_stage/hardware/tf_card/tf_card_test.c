#include "tf_card.h"

#include <stdio.h>
#include <stdlib.h>

int main(void)
{
    const char *device = getenv("TF_DEVICE");
    const char *mount_point = getenv("TF_MOUNT_POINT");
    const char *allow_format = getenv("TF_ALLOW_FORMAT_EXT4");
    struct tf_card_request request;
    struct tf_card_result result;

    if (device == NULL || device[0] == '\0') {
        printf("PENDING code=4300 message=TF_DEVICE is not configured\n");
        return 0;
    }
    if (mount_point == NULL || mount_point[0] == '\0') {
        mount_point = "/mnt/spacetest_tf";
    }

    request.device_path = device;
    request.mount_point = mount_point;
    request.allow_format_ext4 = allow_format != NULL && allow_format[0] == '1';
    request.min_capacity_mb = 0;

    if (tf_card_run_test(&request, &result) == 0) {
        printf("PASS device=%s fs=%s mount=%s total=%lluMB free=%lluMB formatted=%d rw=%d message=%s\n",
               result.device_path, result.filesystem, result.mount_point,
               (unsigned long long)result.total_mb, (unsigned long long)result.free_mb,
               result.formatted ? 1 : 0, result.rw_passed ? 1 : 0, result.message);
        return 0;
    }

    printf("FAIL code=%d device=%s fs=%s mount=%s formatted=%d message=%s\n",
           result.error_code, result.device_path, result.filesystem,
           result.mount_point, result.formatted ? 1 : 0, result.message);
    return 1;
}
