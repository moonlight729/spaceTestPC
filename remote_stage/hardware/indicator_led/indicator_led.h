#ifndef SPACETEST3576_INDICATOR_LED_H
#define SPACETEST3576_INDICATOR_LED_H

#include <stdbool.h>

enum indicator_led_channel {
    INDICATOR_LED_BLUE = 0,
    INDICATOR_LED_GREEN = 1
};

struct indicator_led_device {
    const char *blue_brightness_path;
    const char *green_brightness_path;
};

struct indicator_led_result {
    enum indicator_led_channel channel;
    int brightness;
    bool voltage_meter_verified;
    int error_code;
    char message[160];
};

int indicator_led_open(struct indicator_led_device *device);
void indicator_led_close(struct indicator_led_device *device);
int indicator_led_set(struct indicator_led_device *device,
                      enum indicator_led_channel channel,
                      int brightness,
                      struct indicator_led_result *result);

#endif
