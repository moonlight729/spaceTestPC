#define _POSIX_C_SOURCE 200809L
#include "session_manager.h"

#include "../protocol/protocol.h"
#include "../storage/board_state.h"
#include "../tests/test_runner.h"

#include <stdio.h>
#include <string.h>

static int send_board_state(int fd, const char *session_id)
{
    struct board_state state;
    char data[512];
    char line[1024];
    board_state_load_defaults(&state);
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
    (void)config;

    if (protocol_read_line(client_fd, line, sizeof(line)) <= 0) return -1;
    if (protocol_parse_request(line, &request) != 0) {
        return send_failure(client_fd, "", 1000, "Invalid protocol request");
    }
    if (strcmp(request.protocol_version, "1.0") != 0) {
        return send_failure(client_fd, request.session_id, 1001, "Unsupported protocol version");
    }
    if (strcmp(request.command_group, "sys") == 0 && strcmp(request.command, "get_board_state") == 0) {
        return send_board_state(client_fd, request.session_id);
    }
    if (strcmp(request.command_group, "sys") == 0 && strcmp(request.command, "write_sn") == 0) {
        return write_board_sn(client_fd, &request);
    }
    if (strcmp(request.command_group, "sys") == 0 && strcmp(request.command, "enter_test_mode") == 0) {
        return send_ok_response(client_fd, request.session_id, "Test mode entered");
    }
    if (strcmp(request.command_group, "session") == 0 && strcmp(request.command, "start") == 0)
        return test_runner_run_plan(client_fd, request.session_id, line, config);
    return send_failure(client_fd, request.session_id, 1002, "Unsupported command");
}
