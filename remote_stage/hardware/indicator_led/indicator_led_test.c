#include "indicator_led.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static enum indicator_led_channel parse_channel(const char *value)
{
    if (value != NULL && strcmp(value, "green") == 0) return INDICATOR_LED_GREEN;
    return INDICATOR_LED_BLUE;
}

int main(void)
{
    const char *channel_text = getenv("LED_CHANNEL");
    const char *brightness_text = getenv("LED_BRIGHTNESS");
    struct indicator_led_device device;
    struct indicator_led_result result;
    enum indicator_led_channel channel = parse_channel(channel_text);
    int brightness = brightness_text == NULL ? 0 : atoi(brightness_text);

    if (indicator_led_open(&device) != 0) {
        perror("indicator_led_open");
        return 2;
    }
    if (indicator_led_set(&device, channel, brightness, &result) == 0) {
        printf("PASS channel=%s brightness=%d voltage_meter_verified=%d message=%s\n",
               channel == INDICATOR_LED_GREEN ? "green" : "blue",
               result.brightness, result.voltage_meter_verified ? 1 : 0, result.message);
        indicator_led_close(&device);
        return 0;
    }

    printf("FAIL code=%d channel=%s brightness=%d message=%s\n",
           result.error_code, channel == INDICATOR_LED_GREEN ? "green" : "blue",
           result.brightness, result.message);
    indicator_led_close(&device);
    return 1;
}
