#define _POSIX_C_SOURCE 200809L
#include "session_manager.h"

#include "../protocol/protocol.h"
#include "../storage/board_state.h"
#include "../tests/test_runner.h"

#include <stdio.h>
#include <string.h>

static int send_failure(int fd, const char *session_id, int code, const char *message);
static int send_ok_response(int fd, const char *session_id, const char *message);

static int json_get_string_value(const char *json, const char *key, char *buffer, size_t buffer_size)
{
    char pattern[64];
    const char *start;
    const char *end;
    size_t length;
    if (json == NULL || key == NULL || buffer == NULL || buffer_size == 0) return -1;
    buffer[0] = '\0';
    snprintf(pattern, sizeof(pattern), "\"%s\"", key);
    start = strstr(json, pattern);
    if (start == NULL) return -1;
    start = strchr(start + strlen(pattern), ':');
    if (start == NULL) return -1;
    while (*++start == ' ' || *start == '\t') { }
    if (*start != '"') return -1;
    end = strchr(++start, '"');
    if (end == NULL) return -1;
    length = (size_t)(end - start);
    if (length >= buffer_size) length = buffer_size - 1;
    memcpy(buffer, start, length);
    buffer[length] = '\0';
    return 0;
}

static int handle_sync_session_summary(int fd, const struct protocol_request *request, const char *line, const struct app_config *config)
{
    struct board_test_item_summary items[BOARD_TEST_ITEM_CAPACITY];
    int item_count = 0;
    const char *cursor;
    char verdict[32];

    if (config == NULL) return send_failure(fd, request->session_id, 2200, "Board state config is unavailable");
    memset(items, 0, sizeof(items));
    if (json_get_string_value(line, "finalVerdict", verdict, sizeof(verdict)) != 0) {
        return send_failure(fd, request->session_id, 2201, "Missing finalVerdict");
    }

    cursor = strstr(line, "\"testResults\"");
    while (cursor != NULL && item_count < BOARD_TEST_ITEM_CAPACITY) {
        char test_id[40];
        char status[16];
        if (json_get_string_value(cursor, "testId", test_id, sizeof(test_id)) != 0) break;
        if (json_get_string_value(cursor, "status", status, sizeof(status)) != 0) break;
        snprintf(items[item_count].test_id, sizeof(items[item_count].test_id), "%s", test_id);
        snprintf(items[item_count].last_status, sizeof(items[item_count].last_status), "%s", status);
        items[item_count].test_count = 1;
        item_count++;
        cursor = strstr(cursor + 1, "\"testId\"");
    }

    if (board_state_update_last_result(config->board_state_path, request->session_id, "", "", verdict) != 0) {
        return send_failure(fd, request->session_id, 2202, "Unable to save session summary");
    }
    if (item_count > 0 && board_state_record_test_items(config->board_state_path, items, item_count) != 0) {
        return send_failure(fd, request->session_id, 2203, "Unable to save test item summary");
    }
    return send_ok_response(fd, request->session_id, "Session summary synced");
}

static int send_board_state(int fd, const char *session_id, const struct app_config *config)
{
    struct board_state state;
    char data[4096];
    char line[4608];
    if (config == NULL || board_state_load_from_file(config->board_state_path, &state) != 0) {
        board_state_load_defaults(&state);
    }
    board_state_to_json(&state, data, sizeof(data));
    protocol_build_response_envelope(line, sizeof(line), session_id, 0, "Board state loaded", data);
    return protocol_write_line(fd, line);
}

static int send_failure(int fd, const char *session_id, int code, const char *message)
{
    char line[1024];
    protocol_build_response_envelope(line, sizeof(line), session_id, code, message, "{}");
    return protocol_write_line(fd, line);
}

static int send_ok_response(int fd, const char *session_id, const char *message)
{
    char line[1024];
    protocol_build_response_envelope(line, sizeof(line), session_id, 0, message, "{}");
    return protocol_write_line(fd, line);
}

static int write_board_sn(int fd, const struct protocol_request *request)
{
    int rc = board_state_write_sn_if_empty(request->sn);
    if (rc == 0) return send_ok_response(fd, request->session_id, "Board SN written");
    if (rc == -2) return send_failure(fd, request->session_id, 2101, "Board SN already exists and differs from scanned SN");
    return send_failure(fd, request->session_id, 2100, "Unable to write board SN");
}

int session_manager_handle_client(int client_fd, const struct app_config *config)
{
    char line[PROTOCOL_MAX_LINE];
    struct protocol_request request;
    if (protocol_read_line(client_fd, line, sizeof(line)) <= 0) return -1;
    if (protocol_parse_request(line, &request) != 0) {
        return send_failure(client_fd, "", 1000, "Invalid protocol request");
    }
    if (strcmp(request.protocol_version, "1.0") != 0) {
        return send_failure(client_fd, request.session_id, 1001, "Unsupported protocol version");
    }
    if (strcmp(request.command_group, "sys") == 0 && strcmp(request.command, "get_board_state") == 0) {
        return send_board_state(client_fd, request.session_id, config);
    }
    if (strcmp(request.command_group, "sys") == 0 && strcmp(request.command, "write_sn") == 0) {
        return write_board_sn(client_fd, &request);
    }
    if (strcmp(request.command_group, "sys") == 0 && strcmp(request.command, "enter_test_mode") == 0) {
        return send_ok_response(client_fd, request.session_id, "Test mode entered");
    }
    if (strcmp(request.command_group, "sys") == 0 && strcmp(request.command, "sync_session_summary") == 0) {
        return handle_sync_session_summary(client_fd, &request, line, config);
    }
    if (strcmp(request.command_group, "session") == 0 && strcmp(request.command, "start") == 0)
        return test_runner_run_plan(client_fd, request.session_id, line, config);
    return send_failure(client_fd, request.session_id, 1002, "Unsupported command");
}
