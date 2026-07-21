#include "camera_stream.h"

#include <stdio.h>
#include <stdlib.h>

int main(void)
{
    const char *device = getenv("CAMERA_DEVICE");
    const char *exposure_path = getenv("CAMERA_EXPOSURE_COUNTER");
    struct camera_stream_request request;
    struct camera_stream_result result;

    if (device == NULL || device[0] == '\0') device = "/dev/video0";
    request.device_path = device;
    request.stream_frame_count = 1;
    request.timeout_ms = 3000;
    request.require_exposure_interrupt = exposure_path != NULL && exposure_path[0] != '\0';
    request.exposure_counter_path = exposure_path;
    request.exposure_frame_count = 30;
    request.require_pwm_pulse = 0;
    request.pwm_status_path = getenv("CAMERA_PWM_STATUS");
    request.pwm_min_pulse_delta = 1;

    if (camera_stream_run_test(&request, &result) == 0) {
        printf("PASS device=%s captured=%d exposureDelta=%d streamOk=%d exposureOk=%d pwmDelta=%llu pwmOk=%d message=%s\n",
               result.device_path, result.captured_frames, result.exposure_delta,
               result.stream_ok ? 1 : 0, result.exposure_ok ? 1 : 0,
               result.pwm_pulse_delta, result.pwm_ok ? 1 : 0, result.message);
        return 0;
    }

    printf("FAIL code=%d device=%s captured=%d exposureDelta=%d pwmDelta=%llu message=%s\n",
           result.error_code, result.device_path, result.captured_frames,
           result.exposure_delta, result.pwm_pulse_delta, result.message);
    return 1;
}
