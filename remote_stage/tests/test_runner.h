#ifndef SPACETEST3576_TEST_RUNNER_H
#define SPACETEST3576_TEST_RUNNER_H

#include "../config/app_config.h"

int test_runner_run_plan(int fd, const char *session_id, const char *request_json,
                         const struct app_config *config);

#endif
