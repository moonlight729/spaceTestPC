#define _GNU_SOURCE
#include "key_input.h"

#include <errno.h>
#include <fcntl.h>
#include <linux/input.h>
#include <poll.h>
#include <stddef.h>
#include <string.h>
#include <time.h>
#include <unistd.h>

static const char *const key_devices[KEY_INPUT_DEVICE_COUNT] = {
    "/dev/input/by-path/platform-gpio-keys-event",
    "/dev/input/by-path/platform-2ac40000.i2c-platform-rk805-pwrkey.1.auto-event",
};

static enum key_input_id map_key(unsigned short code)
{
    switch (code) {
    case KEY_UP:       return KEY_INPUT_UP;
    case KEY_DOWN:     return KEY_INPUT_DOWN;
    case KEY_PREVIOUS: return KEY_INPUT_LEFT;
    case KEY_NEXT:     return KEY_INPUT_RIGHT;
    case KEY_POWER:    return KEY_INPUT_CONFIRM;
    default:           return KEY_INPUT_UNKNOWN;
    }
}

const char *key_input_name(enum key_input_id key)
{
    static const char *const names[] = { "up", "down", "left", "right", "confirm", "unknown" };
    return key >= KEY_INPUT_UP && key <= KEY_INPUT_UNKNOWN ? names[key] : names[KEY_INPUT_UNKNOWN];
}

int key_input_open(struct key_input *input)
{
    int i;
    if (input == NULL) {
        errno = EINVAL;
        return -1;
    }
    for (i = 0; i < KEY_INPUT_DEVICE_COUNT; ++i) {
        input->fds[i] = -1;
    }
    for (i = 0; i < KEY_INPUT_DEVICE_COUNT; ++i) {
        input->fds[i] = open(key_devices[i], O_RDONLY | O_NONBLOCK | O_CLOEXEC);
        if (input->fds[i] < 0) {
            key_input_close(input);
            return -1;
        }
    }
    return 0;
}

void key_input_drain_pending(struct key_input *input)
{
    struct input_event raw;
    int i;

    if (input == NULL) return;
    for (i = 0; i < KEY_INPUT_DEVICE_COUNT; ++i) {
        if (input->fds[i] < 0) continue;
        while (read(input->fds[i], &raw, sizeof(raw)) == (ssize_t)sizeof(raw)) {
            /* Discard stale events that happened before this test item started. */
        }
        if (errno != EAGAIN && errno != EWOULDBLOCK) {
            errno = 0;
        }
    }
}

void key_input_close(struct key_input *input)
{
    int i;
    if (input == NULL) return;
    for (i = 0; i < KEY_INPUT_DEVICE_COUNT; ++i) {
        if (input->fds[i] >= 0) close(input->fds[i]);
        input->fds[i] = -1;
    }
}

int key_input_read_event(struct key_input *input, int timeout_ms, struct key_input_event *event)
{
    struct pollfd poll_fds[KEY_INPUT_DEVICE_COUNT];
    struct input_event raw;
    int i, ready;

    if (input == NULL || event == NULL) {
        errno = EINVAL;
        return -1;
    }
    for (i = 0; i < KEY_INPUT_DEVICE_COUNT; ++i) {
        poll_fds[i].fd = input->fds[i];
        poll_fds[i].events = POLLIN;
        poll_fds[i].revents = 0;
    }
    ready = poll(poll_fds, KEY_INPUT_DEVICE_COUNT, timeout_ms);
    if (ready <= 0) return ready;
    for (i = 0; i < KEY_INPUT_DEVICE_COUNT; ++i) {
        if (!(poll_fds[i].revents & POLLIN)) continue;
        if (read(input->fds[i], &raw, sizeof(raw)) != (ssize_t)sizeof(raw)) return -1;
        if (raw.type != EV_KEY) return 0;
        event->key = map_key(raw.code);
        if (event->key == KEY_INPUT_UNKNOWN) return 0;
        event->pressed = raw.value == 1; /* value=2 is auto-repeat and never counts as a pass */
        event->raw_code = raw.code;
        event->timestamp_ms = (int64_t)raw.time.tv_sec * 1000 + raw.time.tv_usec / 1000;
        return 1;
    }
    return 0;
}

int key_input_collect_five_keys(struct key_input *input, int timeout_ms, uint32_t *detected_mask)
{
    const uint32_t expected = (1U << (KEY_INPUT_CONFIRM + 1)) - 1U;
    struct key_input_event event;
    struct timespec start, now;
    int remaining, result;

    if (detected_mask == NULL) {
        errno = EINVAL;
        return -1;
    }
    *detected_mask = 0;
    clock_gettime(CLOCK_MONOTONIC, &start);
    for (;;) {
        clock_gettime(CLOCK_MONOTONIC, &now);
        remaining = timeout_ms < 0 ? -1 : timeout_ms - (int)((now.tv_sec - start.tv_sec) * 1000 + (now.tv_nsec - start.tv_nsec) / 1000000);
        if (timeout_ms >= 0 && remaining <= 0) return 0;
        result = key_input_read_event(input, remaining, &event);
        if (result < 0) return -1;
        if (result == 0) return 0;
        if (event.pressed) *detected_mask |= 1U << event.key;
        if (*detected_mask == expected) return 1;
    }
}
