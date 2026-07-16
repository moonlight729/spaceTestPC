#ifndef SPACETEST3576_CAMERA_STREAM_H
#define SPACETEST3576_CAMERA_STREAM_H

#include <stdbool.h>

struct camera_stream_request {
    const char *device_path;
    int stream_frame_count;
    int timeout_ms;
    bool require_exposure_interrupt;
    const char *exposure_counter_path;
    int exposure_frame_count;
};

struct camera_stream_result {
    char device_path[128];
    int captured_frames;
    int exposure_delta;
    bool stream_ok;
    bool exposure_ok;
    int error_code;
    char message[192];
};

int camera_stream_run_test(const struct camera_stream_request *request,
                           struct camera_stream_result *result);

#endif
