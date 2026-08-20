#ifndef SPACETEST3576_PRESSURE_PERIPHERAL_H
#define SPACETEST3576_PRESSURE_PERIPHERAL_H

struct pressure_peripheral_result {
    int value;
    int active;
    int error_code;
    char detail[128];
};

int pressure_check_fan(const char *pwm_path, const char *tach_path, struct pressure_peripheral_result *result);
int pressure_check_hdmi(struct pressure_peripheral_result *result);
int pressure_check_usb_storage(struct pressure_peripheral_result *result);
int pressure_check_file_storage(const char *directory, const char *label, struct pressure_peripheral_result *result);

#endif
