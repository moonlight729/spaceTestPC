#ifndef SPACETEST3576_BOARD_STATE_H
#define SPACETEST3576_BOARD_STATE_H

#include <stddef.h>

struct board_state {
    char board_id[64];
    char board_sn[80];
    char test_mode[32];
    char current_state[32];
    char last_session_id[80];
    char last_start_time[40];
    char last_end_time[40];
    char last_verdict[32];
    int pass_count;
    int fail_count;
    int version;
};

void board_state_load_defaults(struct board_state *state);
int board_state_write_sn_if_empty(const char *sn);
int board_state_to_json(const struct board_state *state, char *buffer, size_t buffer_size);

#endif
