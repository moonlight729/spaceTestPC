#ifndef SPACETEST3576_PROTOCOL_H
#define SPACETEST3576_PROTOCOL_H

#include <stddef.h>

#define PROTOCOL_MAX_LINE 8192

struct protocol_request {
    char protocol_version[16];
    char session_id[80];
    char sn[80];
    char command_group[40];
    char command[40];
};

int protocol_read_line(int fd, char *buffer, size_t buffer_size);
int protocol_write_line(int fd, const char *line);
int protocol_parse_request(const char *json, struct protocol_request *request);
int protocol_build_test_report(char *buffer, size_t buffer_size,
                               const char *test_id, const char *status,
                               int result_code, const char *message,
                               const char *data_json);
int protocol_build_session_completed(char *buffer, size_t buffer_size,
                                     const char *session_id,
                                     const char *status,
                                     int result_code,
                                     const char *message);
int protocol_build_response_envelope(char *buffer, size_t buffer_size,
                                     const char *session_id,
                                     int result_code,
                                     const char *message,
                                     const char *data_json);

#endif
