#ifndef SPACETEST3576_APP_CONFIG_H
#define SPACETEST3576_APP_CONFIG_H

struct app_config {
    const char *bind_address;
    int port;
    const char *board_state_path;
    const char *wifi_ssid;
    const char *wifi_router_ip;
    const char *tf_device_path;
    const char *tf_mount_point;
    int tf_allow_format_ext4;
    const char *bluetooth_target_name;
    int bluetooth_min_rssi;
    int fast_charge_voltage_min_mv;
    int fast_charge_voltage_max_mv;
    int fast_charge_current_min_ma;
    int fast_charge_current_max_ma;
    int keys_timeout_ms;
    const char *camera_device_path;
    const char *camera_exposure_counter_path;
    int camera_require_exposure_interrupt;
    int camera_stream_frame_count;
    int camera_exposure_frame_count;
    const char *camera_pwm_status_path;
    int camera_require_pwm_pulse;
    int camera_pwm_min_pulse_delta;
};

void app_config_load_defaults(struct app_config *config);

#endif
