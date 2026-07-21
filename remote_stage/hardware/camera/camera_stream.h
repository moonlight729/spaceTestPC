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
    bool require_pwm_pulse;
    const char *pwm_status_path;
    int pwm_min_pulse_delta;
};

struct camera_stream_result {
    char device_path[128];
    int captured_frames;
    int exposure_delta;
    bool stream_ok;
    bool exposure_ok;
    unsigned long long pwm_pulse_count_before;
    unsigned long long pwm_pulse_count_after;
    unsigned long long pwm_pulse_delta;
    long long pwm_mono_ns;
    long long pwm_rtc_ns;
    bool pwm_ok;
    int error_code;
    char message[192];
};

int camera_stream_run_test(const struct camera_stream_request *request,
                           struct camera_stream_result *result);

#endif
