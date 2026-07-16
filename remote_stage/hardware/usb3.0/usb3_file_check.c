#define _POSIX_C_SOURCE 200809L
#include "usb3_file_check.h"

#include <errno.h>
#include <stdio.h>
#include <string.h>
#include <time.h>

static void set_message(struct usb3_result *result, int code, const char *message)
{
    size_t length;
    result->error_code = code;
    length = strnlen(message, sizeof(result->message) - 1);
    memcpy(result->message, message, length);
    result->message[length] = '\0';
}

static void set_ports_message(struct usb_ports_result *result, int code, const char *message)
{
    size_t length;
    result->error_code = code;
    length = strnlen(message, sizeof(result->message) - 1);
    memcpy(result->message, message, length);
    result->message[length] = '\0';
}

static int read_int_file(const char *path, int *value)
{
    FILE *file;
    if (path == NULL || value == NULL) {
        errno = EINVAL;
        return -1;
    }
    file = fopen(path, "r");
    if (file == NULL) return -1;
    if (fscanf(file, "%d", value) != 1) {
        fclose(file);
        errno = EIO;
        return -1;
    }
    fclose(file);
    return 0;
}

static void sleep_ms(int ms)
{
    struct timespec ts;
    ts.tv_sec = ms / 1000;
    ts.tv_nsec = (long)(ms % 1000) * 1000000L;
    nanosleep(&ts, NULL);
}

static int read_text_file(const char *path, char *buffer, size_t buffer_size)
{
    FILE *file;
    size_t used;
    if (path == NULL || buffer == NULL || buffer_size == 0) {
        errno = EINVAL;
        return -1;
    }
    file = fopen(path, "r");
    if (file == NULL) return -1;
    used = fread(buffer, 1, buffer_size - 1, file);
    buffer[used] = '\0';
    fclose(file);
    return used == 0 ? -1 : 0;
}

static int parse_json_int(const char *json, const char *key, int *value)
{
    char pattern[80];
    const char *found;
    const char *colon;
    if (json == NULL || key == NULL || value == NULL) return -1;
    snprintf(pattern, sizeof(pattern), "\"%s\"", key);
    found = strstr(json, pattern);
    if (found == NULL) return -1;
    colon = strchr(found + strlen(pattern), ':');
    if (colon == NULL) return -1;
    return sscanf(colon + 1, "%d", value) == 1 ? 0 : -1;
}

int usb3_open(struct usb3_device *device)
{
    if (device == NULL) {
        errno = EINVAL;
        return -1;
    }
    device->present_path = NULL;
    device->speed_path = NULL;
    device->rw_check_path = NULL;
    return 0;
}

void usb3_close(struct usb3_device *device)
{
    if (device != NULL) {
        device->present_path = NULL;
        device->speed_path = NULL;
        device->rw_check_path = NULL;
    }
}

int usb3_configure_paths(struct usb3_device *device,
                         const char *present_path,
                         const char *speed_path,
                         const char *rw_check_path)
{
    if (device == NULL) {
        errno = EINVAL;
        return -1;
    }
    device->present_path = present_path;
    device->speed_path = speed_path;
    device->rw_check_path = rw_check_path;
    return 0;
}

int usb3_read(struct usb3_device *device, struct usb3_result *result)
{
    int present;
    int speed_mbps;
    int rw_ok = 0;

    if (device == NULL || result == NULL) {
        errno = EINVAL;
        return -1;
    }
    memset(result, 0, sizeof(*result));
    /*
     * Current framework reads only the latest USB state.  The final board file
     * interface should expose USB2.0 and USB3.0 plug history, including four
     * complete insertion/removal cycles for each mode.  When those files exist,
     * extend this reader to parse the history and let usb3_run_test() judge the
     * four-cycle requirement.  Until then, an unconfigured device returns 4500.
     */
    if (device->present_path == NULL || device->speed_path == NULL) {
        set_message(result, 4500, "USB3 file interface is not configured");
        errno = ENODEV;
        return -1;
    }
    if (read_int_file(device->present_path, &present) != 0 ||
        read_int_file(device->speed_path, &speed_mbps) != 0) {
        set_message(result, 4501, "Unable to read USB3 state files");
        return -1;
    }
    if (device->rw_check_path != NULL) {
        if (read_int_file(device->rw_check_path, &rw_ok) != 0) {
            set_message(result, 4502, "Unable to read USB3 read/write check file");
            return -1;
        }
        result->rw_checked = rw_ok != 0;
    }
    result->present = present != 0;
    result->speed_mbps = speed_mbps;
    set_message(result, 0, "USB3 state read successfully");
    return 0;
}

int usb3_run_test(struct usb3_device *device,
                  const struct usb3_request *request,
                  struct usb3_result *result)
{
    if (device == NULL || request == NULL || result == NULL) {
        errno = EINVAL;
        return -1;
    }
    if (usb3_read(device, result) != 0) return -1;
    if (!result->present) {
        set_message(result, 4503, "USB3 device is not present");
        return -1;
    }
    if (result->speed_mbps < request->expected_min_speed_mbps) {
        set_message(result, 4504, "USB3 negotiated speed is below the configured limit");
        return -1;
    }
    if (request->require_rw_check && !result->rw_checked) {
        set_message(result, 4505, "USB3 read/write check did not pass");
        return -1;
    }
    set_message(result, 0, "USB3 check passed");
    return 0;
}

int usb_ports_run_test(const struct usb_ports_request *request,
                       struct usb_ports_result *result)
{
    char content[4096];
    int elapsed = 0;

    if (request == NULL || result == NULL || request->record_file == NULL) {
        errno = EINVAL;
        return -1;
    }
    memset(result, 0, sizeof(*result));
    snprintf(result->record_file, sizeof(result->record_file), "%s", request->record_file);
    result->expected_usb2_count = request->expected_usb2_count;
    result->expected_usb3_count = request->expected_usb3_count;

    while (read_text_file(request->record_file, content, sizeof(content)) != 0) {
        if (elapsed >= request->timeout_ms) {
            set_ports_message(result, 4901, "USB2.0&3.0 record file not found");
            return -1;
        }
        sleep_ms(200);
        elapsed += 200;
    }

    if (parse_json_int(content, "usb2Count", &result->usb2_count) != 0 ||
        parse_json_int(content, "usb3Count", &result->usb3_count) != 0) {
        set_ports_message(result, 4900, "USB2.0&3.0 record file is invalid");
        return -1;
    }
    if (result->usb2_count < request->expected_usb2_count) {
        set_ports_message(result, 4902, "USB2.0 device count is not enough");
        return -1;
    }
    if (result->usb3_count < request->expected_usb3_count) {
        set_ports_message(result, 4903, "USB3.0 device count is not enough");
        return -1;
    }
    set_ports_message(result, 0, "USB2.0&3.0 record loaded");
    return 0;
}
