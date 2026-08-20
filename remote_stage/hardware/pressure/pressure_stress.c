#include "pressure_stress.h"

#include <stdio.h>
#include <stdlib.h>
#include <sys/wait.h>

static int run_stress_ng(const char *argument, int duration_sec, struct pressure_stress_result *result)
{
    char command[256];
    int status;
    if (result == NULL || duration_sec <= 0) return -1;
    snprintf(command, sizeof(command), "stress-ng %s --timeout %ds --metrics-brief >/dev/null 2>&1", argument, duration_sec);
    status = system(command);
    result->duration_sec = duration_sec;
    result->exit_code = status == -1 ? -1 : WEXITSTATUS(status);
    result->error_count = result->exit_code == 0 ? 0 : 1;
    return result->exit_code == 0 ? 0 : -1;
}

int pressure_run_cpu(int workers, int duration_sec, struct pressure_stress_result *result)
{
    char argument[64];
    if (workers < 1) workers = 1;
    snprintf(argument, sizeof(argument), "--cpu %d", workers);
    return run_stress_ng(argument, duration_sec, result);
}

int pressure_run_memory(int memory_mib, int duration_sec, struct pressure_stress_result *result)
{
    char argument[96];
    if (memory_mib < 64) memory_mib = 64;
    snprintf(argument, sizeof(argument), "--vm 1 --vm-bytes %dM --vm-keep", memory_mib);
    return run_stress_ng(argument, duration_sec, result);
}
