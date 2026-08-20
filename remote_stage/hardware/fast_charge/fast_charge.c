#include "fast_charge.h"
#include <string.h>
int fast_charge_open(struct fast_charge_device *device) { (void)device; return -1; }
int fast_charge_run_test(struct fast_charge_device *device, const struct fast_charge_request *request, struct fast_charge_result *result) { (void)device; (void)request; memset(result, 0, sizeof(*result)); result->error_code = 4400; strcpy(result->message, "Fast charge module disabled"); return -1; }
void fast_charge_close(struct fast_charge_device *device) { (void)device; }
