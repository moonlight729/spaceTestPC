#define _POSIX_C_SOURCE 200809L
#include "indicator_led.h"

#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

#define LED_BLUE_BRIGHTNESS_PATH "/sys/class/leds/ledb/brightness"
/*
 * Current board mapping: ledb drives the physical blue LED and ledg is
 * temporarily wired/driven as the physical red LED.  There is no ledr node
 * on this board.  The physical green LED is the charging indicator and is
 * controlled through the I2C command below, not through sysfs.
 */
#define LED_RED_BRIGHTNESS_PATH "/sys/class/leds/ledg/brightness"

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
    /* Green has no sysfs path on this board; it is controlled by I2C. */
    if (channel == INDICATOR_LED_GREEN) return NULL;
    if (channel == INDICATOR_LED_RED) return device->red_brightness_path;
    return NULL;
}

static long elapsed_ms(const struct timespec *start, const struct timespec *now)
{
    return (now->tv_sec - start->tv_sec) * 1000L +
           (now->tv_nsec - start->tv_nsec) / 1000000L;
}

int indicator_led_set_charge(bool enabled, int timeout_ms, int retry_interval_ms)
{
    const char *command = enabled
        ? "i2ctransfer -f -y 7 w2@0x6b 0x13 0x01"
        : "i2ctransfer -f -y 7 w2@0x6b 0x13 0x11";
    struct timespec start, now;
    if (timeout_ms <= 0) timeout_ms = 3000;
    if (retry_interval_ms < 0) retry_interval_ms = 100;
    clock_gettime(CLOCK_MONOTONIC, &start);
    do {
        if (system(command) == 0) return 0;
        {
            struct timespec delay = {
                .tv_sec = retry_interval_ms / 1000,
                .tv_nsec = (long)(retry_interval_ms % 1000) * 1000000L
            };
            nanosleep(&delay, NULL);
        }
        clock_gettime(CLOCK_MONOTONIC, &now);
    } while (elapsed_ms(&start, &now) < timeout_ms);
    return -1;
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
    device->red_brightness_path = LED_RED_BRIGHTNESS_PATH;
    return 0;
}

void indicator_led_close(struct indicator_led_device *device)
{
    if (device != NULL) {
        device->blue_brightness_path = NULL;
        device->red_brightness_path = NULL;
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
