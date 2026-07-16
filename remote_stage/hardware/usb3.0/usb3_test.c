#include "usb3_file_check.h"

#include <stdio.h>

int main(void)
{
    struct usb3_device device;
    struct usb3_request request = {
        .expected_min_speed_mbps = 5000,
        .require_rw_check = false,
    };
    struct usb3_result result;

    if (usb3_open(&device) != 0) {
        perror("usb3_open");
        return 2;
    }

    /*
     * The current build only validates the module framework.  The final
     * production test should call usb3_configure_paths() with board files.
     */
    if (usb3_run_test(&device, &request, &result) == 0) {
        printf("PASS speed=%dMbps message=%s\n", result.speed_mbps, result.message);
        usb3_close(&device);
        return 0;
    }

    printf("PENDING code=%d message=%s\n", result.error_code, result.message);
    usb3_close(&device);
    return result.error_code == 4500 ? 0 : 1;
}
