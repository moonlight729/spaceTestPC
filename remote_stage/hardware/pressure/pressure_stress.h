#ifndef SPACETEST3576_PRESSURE_STRESS_H
#define SPACETEST3576_PRESSURE_STRESS_H

struct pressure_stress_result {
    int exit_code;
    int duration_sec;
    int error_count;
};

int pressure_run_cpu(int workers, int duration_sec, struct pressure_stress_result *result);
int pressure_run_memory(int memory_mib, int duration_sec, struct pressure_stress_result *result);

#endif
