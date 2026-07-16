#ifndef SPACETEST3576_KEY_INPUT_H
#define SPACETEST3576_KEY_INPUT_H

#include <stdbool.h>
#include <stdint.h>

#define KEY_INPUT_DEVICE_COUNT 2

enum key_input_id {
    KEY_INPUT_UP,
    KEY_INPUT_DOWN,
    KEY_INPUT_LEFT,
    KEY_INPUT_RIGHT,
    KEY_INPUT_CONFIRM,
    KEY_INPUT_UNKNOWN
};

struct key_input_event {
    enum key_input_id key;
    bool pressed;
    int raw_code;
    int64_t timestamp_ms;
};

struct key_input {
    int fds[KEY_INPUT_DEVICE_COUNT];
};

int key_input_open(struct key_input *input);
void key_input_drain_pending(struct key_input *input);
void key_input_close(struct key_input *input);

/* timeout_ms: -1 waits indefinitely, 0 is non-blocking. */
int key_input_read_event(struct key_input *input, int timeout_ms,
                         struct key_input_event *event);

/* Wait for unique press transitions of all five keys. */
int key_input_collect_five_keys(struct key_input *input, int timeout_ms,
                                uint32_t *detected_mask);

const char *key_input_name(enum key_input_id key);

#endif
