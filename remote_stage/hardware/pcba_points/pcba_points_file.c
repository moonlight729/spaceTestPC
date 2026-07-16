#define _POSIX_C_SOURCE 200809L
#include "pcba_points_file.h"

#include <errno.h>
#include <stdio.h>
#include <string.h>
#include <time.h>

static void set_message(struct pcba_points_result *result, int code, const char *message)
{
    size_t length;
    result->error_code = code;
    length = strnlen(message, sizeof(result->message) - 1);
    memcpy(result->message, message, length);
    result->message[length] = '\0';
}

static void sleep_ms(int ms)
{
    struct timespec ts;
    ts.tv_sec = ms / 1000;
    ts.tv_nsec = (long)(ms % 1000) * 1000000L;
    nanosleep(&ts, NULL);
}

static int read_text_file(const char *path, char *buffer, size_t buffer_size)
{
    FILE *file;
    size_t used;
    if (path == NULL || buffer == NULL || buffer_size == 0) {
        errno = EINVAL;
        return -1;
    }
    file = fopen(path, "r");
    if (file == NULL) return -1;
    used = fread(buffer, 1, buffer_size - 1, file);
    buffer[used] = '\0';
    fclose(file);
    return used == 0 ? -1 : 0;
}

static int parse_json_int(const char *json, const char *key, int *value)
{
    char pattern[80];
    const char *found;
    const char *colon;
    if (json == NULL || key == NULL || value == NULL) return -1;
    snprintf(pattern, sizeof(pattern), "\"%s\"", key);
    found = strstr(json, pattern);
    if (found == NULL) return -1;
    colon = strchr(found + strlen(pattern), ':');
    if (colon == NULL) return -1;
    return sscanf(colon + 1, "%d", value) == 1 ? 0 : -1;
}

static int parse_voltage_values(const char *json, struct pcba_points_result *result,
                                int min_mv, int max_mv)
{
    const char *cursor = json;
    int count = 0;
    while ((cursor = strstr(cursor, "\"voltageMv\"")) != NULL && count < 32) {
        const char *colon = strchr(cursor, ':');
        int voltage = 0;
        if (colon == NULL || sscanf(colon + 1, "%d", &voltage) != 1) {
            return -1;
        }
        result->points[count].index = count + 1;
        result->points[count].voltage_mv = voltage;
        result->points[count].min_mv = min_mv;
        result->points[count].max_mv = max_mv;
        result->points[count].passed = voltage >= min_mv && voltage <= max_mv;
        if (result->points[count].passed) {
            result->passed_count++;
        } else {
            result->failed_count++;
        }
        count++;
        cursor = colon + 1;
    }
    result->parsed_count = count;
    return count > 0 ? 0 : -1;
}

int pcba_points_run_test(const struct pcba_points_request *request,
                         struct pcba_points_result *result)
{
    char content[8192];
    int elapsed = 0;
    int file_channel_count = 0;
    int requested_count;

    if (request == NULL || result == NULL || request->record_file == NULL) {
        errno = EINVAL;
        return -1;
    }

    memset(result, 0, sizeof(*result));
    snprintf(result->record_file, sizeof(result->record_file), "%s", request->record_file);
    requested_count = request->channel_count <= 0 ? 32 : request->channel_count;
    if (requested_count > 32) requested_count = 32;
    result->channel_count = requested_count;

    while (read_text_file(request->record_file, content, sizeof(content)) != 0) {
        if (elapsed >= request->timeout_ms) {
            set_message(result, 5001, "PCBA test point record file not found");
            return -1;
        }
        sleep_ms(200);
        elapsed += 200;
    }

    if (parse_json_int(content, "channelCount", &file_channel_count) != 0) {
        set_message(result, 5000, "PCBA test point record file is invalid");
        return -1;
    }
    if (file_channel_count < requested_count) {
        set_message(result, 5002, "PCBA test point channel count is not enough");
        return -1;
    }
    if (parse_voltage_values(content, result, request->default_min_mv, request->default_max_mv) != 0) {
        set_message(result, 5000, "PCBA test point voltage values are invalid");
        return -1;
    }
    if (result->parsed_count < requested_count) {
        set_message(result, 5002, "PCBA test point voltage value count is not enough");
        return -1;
    }
    if (result->failed_count > 0) {
        set_message(result, 5003, "PCBA test point voltage is out of range");
        return -1;
    }

    set_message(result, 0, "PCBA test point voltages are in range");
    return 0;
}
