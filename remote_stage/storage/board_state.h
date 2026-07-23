#ifndef SPACETEST3576_BOARD_STATE_H
#define SPACETEST3576_BOARD_STATE_H

#include <stddef.h>

#define BOARD_TEST_ITEM_CAPACITY 24

struct board_test_item_summary {
    char test_id[40];
    char last_status[16];
    int test_count;
};

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
    struct board_test_item_summary test_items[BOARD_TEST_ITEM_CAPACITY];
    int test_item_count;
};

void board_state_load_defaults(struct board_state *state);
int board_state_load_from_file(const char *path, struct board_state *state);
int board_state_save_to_file(const char *path, const struct board_state *state);
int board_state_write_sn(const char *sn);
int board_state_to_json(const struct board_state *state, char *buffer, size_t buffer_size);
int board_state_record_session_result(const char *path, const char *session_id,
                                      const char *start_time, const char *end_time,
                                      const char *verdict);
int board_state_update_last_result(const char *path, const char *session_id,
                                   const char *start_time, const char *end_time,
                                   const char *verdict);
int board_state_record_test_items(const char *path,
                                  const struct board_test_item_summary *items,
                                  int item_count);
int board_state_write_last_result_json(const char *path, const char *session_id,
                                       const char *verdict,
                                       const struct board_test_item_summary *items,
                                       int item_count);

#endif
