#define _POSIX_C_SOURCE 200809L
#include "board_state.h"

#include <ctype.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define BOARD_SN_PATH "/tmp/spacetest3576_board_sn.txt"
#define VENDOR_STORAGE_TOOL "/usr/bin/vendor_storage"
#define VENDOR_SN_ID "VENDOR_SN_ID"

static int is_valid_sn(const char *sn)
{
    const unsigned char *p = (const unsigned char *)sn;
    size_t len;
    if (sn == NULL) return 0;
    len = strlen(sn);
    if (len == 0 || len >= 80) return 0;
    while (*p != '\0') {
        if (!isalnum(*p) && *p != '-' && *p != '_' && *p != '.') return 0;
        p++;
    }
    return 1;
}

static int parse_vendor_storage_sn_line(const char *line, char *sn, size_t sn_size)
{
    const char *p;
    size_t len;
    if (line == NULL || sn == NULL || sn_size == 0) return -1;

    p = strstr(line, VENDOR_SN_ID ":");
    if (p != NULL) {
        p += strlen(VENDOR_SN_ID ":");
    } else {
        p = line;
    }

    while (*p == ' ' || *p == '\t') p++;
    len = strcspn(p, "\r\n");
    if (len == 0 || len >= sn_size) return -1;
    memcpy(sn, p, len);
    sn[len] = '\0';
    return is_valid_sn(sn) ? 0 : -1;
}

static int read_sn_from_vendor_storage(char *sn, size_t sn_size)
{
    FILE *pipe;
    char line[256];
    char parsed[80];

    if (sn == NULL || sn_size == 0) return -1;
    sn[0] = '\0';

    pipe = popen(VENDOR_STORAGE_TOOL " -r " VENDOR_SN_ID " -t string 2>/dev/null", "r");
    if (pipe == NULL) return -1;
    while (fgets(line, sizeof(line), pipe) != NULL) {
        if (parse_vendor_storage_sn_line(line, parsed, sizeof(parsed)) == 0) {
            snprintf(sn, sn_size, "%s", parsed);
        }
    }
    if (pclose(pipe) == -1) return -1;
    return sn[0] == '\0' ? -1 : 0;
}

static int write_sn_to_vendor_storage(const char *sn)
{
    char command[256];
    int rc;

    if (!is_valid_sn(sn)) return -1;
    snprintf(command, sizeof(command), VENDOR_STORAGE_TOOL " -w " VENDOR_SN_ID " -t string -i '%s'", sn);
    rc = system(command);
    return rc == 0 ? 0 : -1;
}

void board_state_load_defaults(struct board_state *state)
{
    memset(state, 0, sizeof(*state));
    snprintf(state->board_id, sizeof(state->board_id), "rk3576");
    snprintf(state->test_mode, sizeof(state->test_mode), "ready");
    snprintf(state->current_state, sizeof(state->current_state), "idle");
    state->version = 1;
    if (read_sn_from_vendor_storage(state->board_sn, sizeof(state->board_sn)) == 0) {
        return;
    }
}

int board_state_write_sn_if_empty(const char *sn)
{
    struct board_state state;
    FILE *file;
    char verify_sn[80];
    if (!is_valid_sn(sn)) return -1;
    board_state_load_defaults(&state);
    if (state.board_sn[0] != '\0' && strcmp(state.board_sn, sn) != 0) return -2;
    if (write_sn_to_vendor_storage(sn) != 0) return -1;
    if (read_sn_from_vendor_storage(verify_sn, sizeof(verify_sn)) != 0) return -1;
    if (strcmp(verify_sn, sn) != 0) return -1;

    /* Keep a temporary mirror only for diagnostics and old smoke scripts.
     * The authoritative SN source is Rockchip vendor storage VENDOR_SN_ID.
     */
    file = fopen(BOARD_SN_PATH, "w");
    if (file != NULL) {
        if (fprintf(file, "%s\n", sn) < 0) {
            fclose(file);
            return -1;
        }
        fclose(file);
    }
    return 0;
}

int board_state_to_json(const struct board_state *state, char *buffer, size_t buffer_size)
{
    return snprintf(buffer, buffer_size,
                    "{\"boardId\":\"%s\",\"boardSn\":\"%s\",\"testMode\":\"%s\",\"currentState\":\"%s\",\"lastSessionId\":\"%s\",\"lastStartTime\":\"%s\",\"lastEndTime\":\"%s\",\"lastVerdict\":\"%s\",\"passCount\":%d,\"failCount\":%d,\"totalCount\":%d,\"version\":%d}",
                    state->board_id, state->board_sn, state->test_mode,
                    state->current_state, state->last_session_id,
                    state->last_start_time, state->last_end_time,
                    state->last_verdict, state->pass_count, state->fail_count,
                    state->pass_count + state->fail_count, state->version);
}
