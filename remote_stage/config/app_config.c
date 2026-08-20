#include "app_config.h"
#include "version.h"

#include <stdlib.h>

static const char g_spacetest_version[] __attribute__((used)) = SPACETEST_VERSION_STRING;

void app_config_load_defaults(struct app_config *config)
{
    const char *keys_timeout_ms;
    const char *port;
    const char *bind_address;
    config->bind_address = "0.0.0.0";
    config->port = 19001;
    config->board_state_path = "/userdata/factory_test/spacetest3576_board_state.txt";
    config->wifi_ssid = "originflow";
    config->wifi_router_ip = "192.168.110.1";
    config->tf_device_path = "/dev/mmcblk1p1";
    config->tf_mount_point = "/mnt/spacetest_tf";
    config->tf_allow_format_ext4 = 1;
    config->bluetooth_target_name = "yctc_bt_01";
    config->bluetooth_min_rssi = -60;
    config->fast_charge_voltage_min_mv = 7500;
    config->fast_charge_voltage_max_mv = 12000;
    config->fast_charge_current_min_ma = 0;
    config->fast_charge_current_max_ma = 5000;
    config->keys_timeout_ms = 30000;
    config->camera_device_path = "/dev/video0";
    config->camera_exposure_counter_path = NULL;
    config->camera_require_exposure_interrupt = 0;
    config->camera_stream_frame_count = 90;
    config->camera_exposure_frame_count = 30;
    config->camera_pwm_status_path = "/sys/devices/platform/sync-pwm/status_bin";
    config->camera_require_pwm_pulse = 1;
    config->camera_pwm_min_pulse_delta = 86;
    config->application_path = "/vendor/originflow/bin/spacetest3576";
    config->application_service = "pcba-test.service";
    config->application_version = SPACETEST_VERSION;
    /* USB pretest is retained but disabled until the hardware flow is finalized. */
    config->usb_pretest_enabled = 0;
    config->usb_pretest_http_port = 18080;
    if (getenv("SPACETEST_USB_PRETEST_ENABLED") != NULL &&
        atoi(getenv("SPACETEST_USB_PRETEST_ENABLED")) != 0) {
        config->usb_pretest_enabled = 1;
    }
    bind_address = getenv("SPACETEST_BIND_ADDRESS");
    if (bind_address != NULL && bind_address[0] != '\0') {
        config->bind_address = bind_address;
    }
    port = getenv("SPACETEST_PORT");
    if (port != NULL && port[0] != '\0') {
        int value = atoi(port);
        if (value > 0 && value <= 65535) config->port = value;
    }
    keys_timeout_ms = getenv("SPACETEST_KEYS_TIMEOUT_MS");
    if (keys_timeout_ms != NULL && keys_timeout_ms[0] != '\0') {
        int value = atoi(keys_timeout_ms);
        if (value > 0) config->keys_timeout_ms = value;
    }
}
