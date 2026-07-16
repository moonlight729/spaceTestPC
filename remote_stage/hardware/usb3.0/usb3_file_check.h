#ifndef SPACETEST3576_USB3_FILE_CHECK_H
#define SPACETEST3576_USB3_FILE_CHECK_H

#include <stdbool.h>

struct usb3_device {
    /*
     * Future board software should expose stable files for USB plug history.
     * This module is reserved for reading USB2.0 and USB3.0 plug/unplug records,
     * especially the four insertion cycles required by production test.
     *
     * Planned file semantics:
     * - present_path: current USB device presence, non-zero means inserted.
     * - speed_path: current negotiated speed in Mbps, e.g. 480 for USB2.0
     *   and 5000 or higher for USB3.0.
     * - rw_check_path: optional read/write check status.
     *
     * Later we should add dedicated history paths or replace these paths with
     * a structured board file that records four USB2.0 cycles and four USB3.0
     * cycles. Keep this API configurable so the manage layer can adapt without
     * changing the upper-PC protocol.
     */
    const char *present_path;
    const char *speed_path;
    const char *rw_check_path;
};

struct usb3_request {
    int expected_min_speed_mbps;
    bool require_rw_check;
};

struct usb3_result {
    bool present;
    int speed_mbps;
    bool rw_checked;
    int error_code;
    char message[160];
};

struct usb_ports_request {
    const char *record_file;
    int expected_usb2_count;
    int expected_usb3_count;
    int timeout_ms;
};

struct usb_ports_result {
    char record_file[160];
    int usb2_count;
    int usb3_count;
    int expected_usb2_count;
    int expected_usb3_count;
    int error_code;
    char message[160];
};

int usb3_open(struct usb3_device *device);
void usb3_close(struct usb3_device *device);
int usb3_configure_paths(struct usb3_device *device,
                         const char *present_path,
                         const char *speed_path,
                         const char *rw_check_path);
int usb3_read(struct usb3_device *device, struct usb3_result *result);
int usb3_run_test(struct usb3_device *device,
                  const struct usb3_request *request,
                  struct usb3_result *result);
int usb_ports_run_test(const struct usb_ports_request *request,
                       struct usb_ports_result *result);

#endif
