#include "app_config.h"

#include <stdlib.h>

void app_config_load_defaults(struct app_config *config)
{
    const char *keys_timeout_ms;
    config->bind_address = "127.0.0.1";
    config->port = 19001;
    config->board_state_path = "/userdata/factory_test/spacetest3576_board_state.txt";
    config->wifi_ssid = "originflow";
    config->wifi_router_ip = "192.168.110.1";
    config->tf_device_path = "/dev/mmcblk1p1";
    config->tf_mount_point = "/mnt/spacetest_tf";
    config->tf_allow_format_ext4 = 1;
    config->bluetooth_target_name = "yctc_bt_01";
    config->bluetooth_min_rssi = -80;
    config->fast_charge_voltage_min_mv = 7500;
    config->fast_charge_voltage_max_mv = 12000;
    config->fast_charge_current_min_ma = 0;
    config->fast_charge_current_max_ma = 5000;
    config->keys_timeout_ms = 30000;
    config->camera_device_path = "/dev/video0";
    config->camera_exposure_counter_path = NULL;
    config->camera_require_exposure_interrupt = 0;
    config->camera_stream_frame_count = 1;
    config->camera_exposure_frame_count = 30;
    config->camera_pwm_status_path = "/sys/devices/platform/sync-pwm/status_bin";
    config->camera_require_pwm_pulse = 0;
    config->camera_pwm_min_pulse_delta = 1;
    config->application_path = "/vendor/originflow/bin/spacetest3576";
    config->application_service = "pcba-test.service";
    keys_timeout_ms = getenv("SPACETEST_KEYS_TIMEOUT_MS");
    if (keys_timeout_ms != NULL && keys_timeout_ms[0] != '\0') {
        int value = atoi(keys_timeout_ms);
        if (value > 0) config->keys_timeout_ms = value;
    }
}
