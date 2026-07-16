#define _POSIX_C_SOURCE 200809L
#include "indicator_led.h"

#include <errno.h>
#include <stdio.h>
#include <string.h>

#define LED_BLUE_BRIGHTNESS_PATH "/sys/class/leds/ledb/brightness"
#define LED_GREEN_BRIGHTNESS_PATH "/sys/class/leds/ledg/brightness"

static void set_message(struct indicator_led_result *result, int code, const char *message)
{
    size_t length;
    result->error_code = code;
    length = strnlen(message, sizeof(result->message) - 1);
    memcpy(result->message, message, length);
    result->message[length] = '\0';
}

static const char *channel_path(struct indicator_led_device *device,
                                enum indicator_led_channel channel)
{
    if (channel == INDICATOR_LED_BLUE) return device->blue_brightness_path;
    if (channel == INDICATOR_LED_GREEN) return device->green_brightness_path;
    return NULL;
}

static int write_brightness(const char *path, int brightness)
{
    FILE *file;
    if (path == NULL || brightness < 0) {
        errno = EINVAL;
        return -1;
    }
    file = fopen(path, "w");
    if (file == NULL) return -1;
    if (fprintf(file, "%d\n", brightness) < 0) {
        fclose(file);
        return -1;
    }
    fclose(file);
    return 0;
}

int indicator_led_open(struct indicator_led_device *device)
{
    if (device == NULL) {
        errno = EINVAL;
        return -1;
    }
    device->blue_brightness_path = LED_BLUE_BRIGHTNESS_PATH;
    device->green_brightness_path = LED_GREEN_BRIGHTNESS_PATH;
    return 0;
}

void indicator_led_close(struct indicator_led_device *device)
{
    if (device != NULL) {
        device->blue_brightness_path = NULL;
        device->green_brightness_path = NULL;
    }
}

int indicator_led_set(struct indicator_led_device *device,
                      enum indicator_led_channel channel,
                      int brightness,
                      struct indicator_led_result *result)
{
    const char *path;
    if (device == NULL || result == NULL) {
        errno = EINVAL;
        return -1;
    }
    memset(result, 0, sizeof(*result));
    result->channel = channel;
    result->brightness = brightness;

    path = channel_path(device, channel);
    if (path == NULL) {
        set_message(result, 4600, "Unknown indicator LED channel");
        errno = EINVAL;
        return -1;
    }
    if (write_brightness(path, brightness) != 0) {
        set_message(result, 4601, "Unable to write indicator LED brightness");
        return -1;
    }

    /*
     * This is only the hardware-output framework.  The final production test
     * must verify the LED board through the voltage tester after brightness is
     * changed.  The manage layer should keep this result as output-controlled
     * until the voltage tester confirms the expected channel voltage.
     */
    result->voltage_meter_verified = false;
    set_message(result, 0, "Indicator LED brightness written");
    return 0;
}
