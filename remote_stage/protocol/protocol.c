#define _POSIX_C_SOURCE 200809L
#include "protocol.h"

#include <errno.h>
#include <stdio.h>
#include <string.h>
#include <time.h>
#include <unistd.h>

static void copy_json_value(const char *json, const char *key, char *dst, size_t dst_size)
{
    char pattern[64];
    const char *start;
    const char *end;
    size_t length;
    if (dst_size == 0) return;
    dst[0] = '\0';
    snprintf(pattern, sizeof(pattern), "\"%s\"", key);
    start = strstr(json, pattern);
    if (start == NULL) return;
    start = strchr(start + strlen(pattern), ':');
    if (start == NULL) return;
    start++;
    while (*start == ' ' || *start == '\t') start++;
    if (*start != '"') return;
    start++;
    end = strchr(start, '"');
    if (end == NULL) return;
    length = (size_t)(end - start);
    if (length >= dst_size) length = dst_size - 1;
    memcpy(dst, start, length);
    dst[length] = '\0';
}

int protocol_read_line(int fd, char *buffer, size_t buffer_size)
{
    size_t used = 0;
    char ch;
    ssize_t read_count;
    if (buffer == NULL || buffer_size < 2) {
        errno = EINVAL;
        return -1;
    }
    while (used + 1 < buffer_size) {
        read_count = read(fd, &ch, 1);
        if (read_count == 0) break;
        if (read_count < 0) return -1;
        if (ch == '\n') break;
        if (ch != '\r') buffer[used++] = ch;
    }
    buffer[used] = '\0';
    return used == 0 ? 0 : (int)used;
}

int protocol_write_line(int fd, const char *line)
{
    size_t length;
    if (line == NULL) {
        errno = EINVAL;
        return -1;
    }
    length = strlen(line);
    if (write(fd, line, length) != (ssize_t)length) return -1;
    if (write(fd, "\n", 1) != 1) return -1;
    return 0;
}

int protocol_parse_request(const char *json, struct protocol_request *request)
{
    if (json == NULL || request == NULL) {
        errno = EINVAL;
        return -1;
    }
    memset(request, 0, sizeof(*request));
    copy_json_value(json, "protocolVersion", request->protocol_version, sizeof(request->protocol_version));
    copy_json_value(json, "sessionId", request->session_id, sizeof(request->session_id));
    copy_json_value(json, "sn", request->sn, sizeof(request->sn));
    copy_json_value(json, "commandGroup", request->command_group, sizeof(request->command_group));
    copy_json_value(json, "command", request->command, sizeof(request->command));
    if (request->protocol_version[0] == '\0' || request->command_group[0] == '\0' ||
        request->command[0] == '\0') {
        errno = EINVAL;
        return -1;
    }
    return 0;
}

int protocol_build_test_report(char *buffer, size_t buffer_size,
                               const char *test_id, const char *status,
                               int result_code, const char *message,
                               const char *data_json)
{
    return snprintf(buffer, buffer_size,
                    "{\"event\":\"test.report\",\"testId\":\"%s\",\"status\":\"%s\",\"resultCode\":%d,\"message\":\"%s\",\"data\":%s}",
                    test_id, status, result_code, message, data_json == NULL ? "{}" : data_json);
}

int protocol_build_session_completed(char *buffer, size_t buffer_size,
                                     const char *session_id,
                                     const char *status,
                                     int result_code,
                                     const char *message)
{
    return snprintf(buffer, buffer_size,
                    "{\"event\":\"session.completed\",\"sessionId\":\"%s\",\"status\":\"%s\",\"resultCode\":%d,\"message\":\"%s\"}",
                    session_id, status, result_code, message);
}

int protocol_build_response_envelope(char *buffer, size_t buffer_size,
                                     const char *session_id,
                                     int result_code,
                                     const char *message,
                                     const char *data_json)
{
    char timestamp[40];
    time_t now = time(NULL);
    struct tm tm_value;
    localtime_r(&now, &tm_value);
    strftime(timestamp, sizeof(timestamp), "%Y-%m-%dT%H:%M:%S%z", &tm_value);
    return snprintf(buffer, buffer_size,
                    "{\"protocolVersion\":\"1.0\",\"requestId\":\"\",\"sessionId\":\"%s\",\"resultCode\":%d,\"message\":\"%s\",\"timestamp\":\"%s\",\"data\":%s}",
                    session_id == NULL ? "" : session_id,
                    result_code,
                    message == NULL ? "" : message,
                    timestamp,
                    data_json == NULL ? "{}" : data_json);
}
