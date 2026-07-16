#ifndef SPACETEST3576_PCBA_POINTS_FILE_H
#define SPACETEST3576_PCBA_POINTS_FILE_H

struct pcba_points_request {
    const char *record_file;
    int channel_count;
    int default_min_mv;
    int default_max_mv;
    int timeout_ms;
};

struct pcba_point_value {
    int index;
    int voltage_mv;
    int min_mv;
    int max_mv;
    int passed;
};

struct pcba_points_result {
    char record_file[160];
    int channel_count;
    int parsed_count;
    int passed_count;
    int failed_count;
    struct pcba_point_value points[32];
    int error_code;
    char message[160];
};

/*
 * PCBA test points are currently exposed through a board-maintained summary
 * file.  The future hardware implementation can replace this file reader with
 * ADC / fixture communication without changing the upper-PC protocol.
 *
 * Expected summary file:
 * {
 *   "channelCount": 32,
 *   "points": [
 *     { "index": 1, "voltageMv": 3300 },
 *     ...
 *   ]
 * }
 */
int pcba_points_run_test(const struct pcba_points_request *request,
                         struct pcba_points_result *result);

#endif
