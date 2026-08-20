#ifndef SPACETEST3576_FAST_CHARGE_H
#define SPACETEST3576_FAST_CHARGE_H
struct fast_charge_device { int unused; };
struct fast_charge_request { int voltage_min_mv; int voltage_max_mv; int current_min_ma; int current_max_ma; int stable_sample_count; int sample_interval_ms; int timeout_ms; };
struct fast_charge_result { int voltage_mv; int current_ma; int stable_samples; int charger_online; int error_code; char message[160]; };
int fast_charge_open(struct fast_charge_device *device);
int fast_charge_run_test(struct fast_charge_device *device, const struct fast_charge_request *request, struct fast_charge_result *result);
void fast_charge_close(struct fast_charge_device *device);
#endif
