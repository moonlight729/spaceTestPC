#define _POSIX_C_SOURCE 200809L
#include "board_state.h"

#include <ctype.h>
#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <time.h>

#define BOARD_STATE_DIR "/userdata/factory_test"
#define BOARD_SN_PATH "/userdata/factory_test/spacetest3576_board_sn.txt"
#define VENDOR_STORAGE_TOOL "/usr/bin/vendor_storage"
#define VENDOR_SN_ID "VENDOR_SN_ID"

static void copy_text(char *destination, size_t destination_size, const char *source)
{
    if (destination == NULL || destination_size == 0) return;
    snprintf(destination, destination_size, "%s", source == NULL ? "" : source);
}

static void trim_line(char *text)
{
    size_t length;
    if (text == NULL) return;
    length = strcspn(text, "\r\n");
    text[length] = '\0';
}

static int parse_int_value(const char *text, int fallback)
{
    int value;
    return text != NULL && sscanf(text, "%d", &value) == 1 ? value : fallback;
}

static int ensure_directory_exists(const char *path)
{
    struct stat st;

    if (path == NULL || path[0] == '\0') return -1;
    if (stat(path, &st) == 0) return S_ISDIR(st.st_mode) ? 0 : -1;
    if (mkdir(path, 0775) == 0) return 0;
    return errno == EEXIST ? 0 : -1;
}

static int ensure_parent_directory(const char *path)
{
    char directory[256];
    const char *slash;
    size_t length;

    if (path == NULL || path[0] == '\0') return -1;
    slash = strrchr(path, '/');
    if (slash == NULL) return 0;
    length = (size_t)(slash - path);
    if (length == 0 || length >= sizeof(directory)) return -1;
    memcpy(directory, path, length);
    directory[length] = '\0';
    return ensure_directory_exists(directory);
}

static int write_sn_mirror_file(const char *sn)
{
    FILE *file;

    if (ensure_directory_exists(BOARD_STATE_DIR) != 0) return -1;
    file = fopen(BOARD_SN_PATH, "w");
    if (file == NULL) return -1;
    if (fprintf(file, "%s\n", sn == NULL ? "" : sn) < 0) {
        fclose(file);
        return -1;
    }
    fclose(file);
    return 0;
}

static int build_last_result_path(const char *board_state_path, char *buffer, size_t buffer_size)
{
    const char *slash;
    size_t directory_length;

    if (board_state_path == NULL || buffer == NULL || buffer_size == 0) return -1;
    slash = strrchr(board_state_path, '/');
    if (slash == NULL) return -1;
    directory_length = (size_t)(slash - board_state_path);
    if (directory_length == 0 || directory_length + strlen("/last_result.json") + 1 > buffer_size) return -1;
    memcpy(buffer, board_state_path, directory_length);
    buffer[directory_length] = '\0';
    strcat(buffer, "/last_result.json");
    return 0;
}

static void format_timestamp_now(char *buffer, size_t buffer_size)
{
    time_t now;
    struct tm tm_value;

    if (buffer == NULL || buffer_size == 0) return;
    now = time(NULL);
    localtime_r(&now, &tm_value);
    strftime(buffer, buffer_size, "%Y-%m-%dT%H:%M:%S%z", &tm_value);
}

static void append_json_escaped(char *buffer, size_t buffer_size, size_t *used, const char *text)
{
    char escaped[128];
    int written;

    if (buffer == NULL || used == NULL || *used >= buffer_size) return;
    copy_text(escaped, sizeof(escaped), text);
    for (char *p = escaped; *p != '\0'; ++p) {
        if (*p == '"' || *p == '\\') *p = '_';
    }
    written = snprintf(buffer + *used, buffer_size - *used, "%s", escaped);
    if (written < 0) return;
    *used += (size_t)written < buffer_size - *used ? (size_t)written : buffer_size - *used - 1;
}

static int find_test_item_index(struct board_state *state, const char *test_id)
{
    int index;
    if (state == NULL || test_id == NULL || test_id[0] == '\0') return -1;
    for (index = 0; index < state->test_item_count; ++index) {
        if (strcmp(state->test_items[index].test_id, test_id) == 0) return index;
    }
    return -1;
}

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

int board_state_load_from_file(const char *path, struct board_state *state)
{
    FILE *file;
    char line[256];

    if (path == NULL || state == NULL) return -1;
    board_state_load_defaults(state);
    file = fopen(path, "r");
    if (file == NULL) return -1;

    while (fgets(line, sizeof(line), file) != NULL) {
        char *equals = strchr(line, '=');
        if (equals == NULL) continue;
        *equals = '\0';
        trim_line(line);
        trim_line(equals + 1);

        if (strcmp(line, "board_id") == 0) {
            if ((equals + 1)[0] != '\0') copy_text(state->board_id, sizeof(state->board_id), equals + 1);
        }
        else if (strcmp(line, "board_sn") == 0) {
            /* Keep the vendor-storage SN authoritative. An empty summary file
             * must not erase a valid SN that was already read from vendor storage.
             */
            if ((equals + 1)[0] != '\0') copy_text(state->board_sn, sizeof(state->board_sn), equals + 1);
        }
        else if (strcmp(line, "test_mode") == 0) {
            if ((equals + 1)[0] != '\0') copy_text(state->test_mode, sizeof(state->test_mode), equals + 1);
        }
        else if (strcmp(line, "current_state") == 0) {
            if ((equals + 1)[0] != '\0') copy_text(state->current_state, sizeof(state->current_state), equals + 1);
        }
        else if (strcmp(line, "last_session_id") == 0) {
            if ((equals + 1)[0] != '\0') copy_text(state->last_session_id, sizeof(state->last_session_id), equals + 1);
        }
        else if (strcmp(line, "last_start_time") == 0) {
            if ((equals + 1)[0] != '\0') copy_text(state->last_start_time, sizeof(state->last_start_time), equals + 1);
        }
        else if (strcmp(line, "last_end_time") == 0) {
            if ((equals + 1)[0] != '\0') copy_text(state->last_end_time, sizeof(state->last_end_time), equals + 1);
        }
        else if (strcmp(line, "last_verdict") == 0) {
            if ((equals + 1)[0] != '\0') copy_text(state->last_verdict, sizeof(state->last_verdict), equals + 1);
        }
        else if (strcmp(line, "pass_count") == 0) state->pass_count = parse_int_value(equals + 1, state->pass_count);
        else if (strcmp(line, "fail_count") == 0) state->fail_count = parse_int_value(equals + 1, state->fail_count);
        else if (strcmp(line, "version") == 0) state->version = parse_int_value(equals + 1, state->version);
        else if (strncmp(line, "test_item_", 10) == 0) {
            int index = -1;
            char field[32];
            if (sscanf(line, "test_item_%d_%31s", &index, field) == 2 &&
                index >= 0 && index < BOARD_TEST_ITEM_CAPACITY) {
                if (index >= state->test_item_count) state->test_item_count = index + 1;
                if (strcmp(field, "id") == 0) copy_text(state->test_items[index].test_id, sizeof(state->test_items[index].test_id), equals + 1);
                else if (strcmp(field, "status") == 0) copy_text(state->test_items[index].last_status, sizeof(state->test_items[index].last_status), equals + 1);
                else if (strcmp(field, "count") == 0) state->test_items[index].test_count = parse_int_value(equals + 1, state->test_items[index].test_count);
            }
        }
    }

    fclose(file);
    return 0;
}

int board_state_save_to_file(const char *path, const struct board_state *state)
{
    char temp_path[256];
    FILE *file;
    int index;

    if (path == NULL || state == NULL) return -1;
    if (ensure_parent_directory(path) != 0) return -1;
    snprintf(temp_path, sizeof(temp_path), "%s.tmp", path);
    file = fopen(temp_path, "w");
    if (file == NULL) return -1;

    if (fprintf(file,
                "board_id=%s\nboard_sn=%s\ntest_mode=%s\ncurrent_state=%s\nlast_session_id=%s\n"
                "last_start_time=%s\nlast_end_time=%s\nlast_verdict=%s\npass_count=%d\nfail_count=%d\nversion=%d\n",
                state->board_id, state->board_sn, state->test_mode, state->current_state,
                state->last_session_id, state->last_start_time, state->last_end_time,
                state->last_verdict, state->pass_count, state->fail_count, state->version) < 0) {
        fclose(file);
        remove(temp_path);
        return -1;
    }

    for (index = 0; index < state->test_item_count; ++index) {
        const struct board_test_item_summary *item = &state->test_items[index];
        if (item->test_id[0] == '\0') continue;
        if (fprintf(file,
                    "test_item_%d_id=%s\ntest_item_%d_status=%s\ntest_item_%d_count=%d\n",
                    index, item->test_id, index, item->last_status, index, item->test_count) < 0) {
            fclose(file);
            remove(temp_path);
            return -1;
        }
    }

    fclose(file);
    if (rename(temp_path, path) != 0) {
        remove(temp_path);
        return -1;
    }
    if (state->board_sn[0] != '\0') {
        write_sn_mirror_file(state->board_sn);
    }
    return 0;
}

int board_state_write_sn_if_empty(const char *sn)
{
    struct board_state state;
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
    if (write_sn_mirror_file(sn) != 0) return -1;
    return 0;
}

int board_state_to_json(const struct board_state *state, char *buffer, size_t buffer_size)
{
    size_t used = 0;
    int index;
    int passed_item_count = 0;
    int failed_item_count = 0;
    int skipped_item_count = 0;
    int first_passed = 1;
    int first_failed = 1;
    int first_skipped = 1;

    used += (size_t)snprintf(buffer, buffer_size,
                             "{\"boardId\":\"%s\",\"boardSn\":\"%s\",\"testMode\":\"%s\",\"currentState\":\"%s\","
                             "\"lastSessionId\":\"%s\",\"lastStartTime\":\"%s\",\"lastEndTime\":\"%s\",\"lastVerdict\":\"%s\","
                             "\"passCount\":%d,\"failCount\":%d,\"totalCount\":%d,\"version\":%d,\"testItems\":[",
                             state->board_id, state->board_sn, state->test_mode,
                             state->current_state, state->last_session_id,
                             state->last_start_time, state->last_end_time,
                             state->last_verdict, state->pass_count, state->fail_count,
                             state->pass_count + state->fail_count, state->version);
    for (index = 0; index < state->test_item_count && used + 1 < buffer_size; ++index) {
        const struct board_test_item_summary *item = &state->test_items[index];
        if (item->test_id[0] == '\0') continue;
        used += (size_t)snprintf(buffer + used, buffer_size - used,
                                 "%s{\"testId\":\"", index == 0 ? "" : ",");
        append_json_escaped(buffer, buffer_size, &used, item->test_id);
        used += (size_t)snprintf(buffer + used, buffer_size - used,
                                 "\",\"lastStatus\":\"");
        append_json_escaped(buffer, buffer_size, &used, item->last_status);
        used += (size_t)snprintf(buffer + used, buffer_size - used,
                                 "\",\"testCount\":%d}", item->test_count);

        if (strcmp(item->last_status, "passed") == 0 || strcmp(item->last_status, "PASS") == 0) passed_item_count++;
        else if (strcmp(item->last_status, "skipped") == 0 || strcmp(item->last_status, "SKIPPED") == 0) skipped_item_count++;
        else if (item->last_status[0] != '\0') failed_item_count++;
    }
    used += (size_t)snprintf(buffer + used, buffer_size - used,
                             "],\"passedItemCount\":%d,\"failedItemCount\":%d,\"skippedItemCount\":%d,"
                             "\"passedItems\":[",
                             passed_item_count, failed_item_count, skipped_item_count);
    for (index = 0; index < state->test_item_count && used + 1 < buffer_size; ++index) {
        const struct board_test_item_summary *item = &state->test_items[index];
        if (!(strcmp(item->last_status, "passed") == 0 || strcmp(item->last_status, "PASS") == 0)) continue;
        used += (size_t)snprintf(buffer + used, buffer_size - used, "%s\"", first_passed ? "" : ",");
        append_json_escaped(buffer, buffer_size, &used, item->test_id);
        used += (size_t)snprintf(buffer + used, buffer_size - used, "\"");
        first_passed = 0;
    }
    used += (size_t)snprintf(buffer + used, buffer_size - used, "],\"failedItems\":[");
    for (index = 0; index < state->test_item_count && used + 1 < buffer_size; ++index) {
        const struct board_test_item_summary *item = &state->test_items[index];
        if (item->last_status[0] == '\0') continue;
        if (strcmp(item->last_status, "passed") == 0 || strcmp(item->last_status, "PASS") == 0 ||
            strcmp(item->last_status, "skipped") == 0 || strcmp(item->last_status, "SKIPPED") == 0) continue;
        used += (size_t)snprintf(buffer + used, buffer_size - used, "%s\"", first_failed ? "" : ",");
        append_json_escaped(buffer, buffer_size, &used, item->test_id);
        used += (size_t)snprintf(buffer + used, buffer_size - used, "\"");
        first_failed = 0;
    }
    used += (size_t)snprintf(buffer + used, buffer_size - used, "],\"skippedItems\":[");
    for (index = 0; index < state->test_item_count && used + 1 < buffer_size; ++index) {
        const struct board_test_item_summary *item = &state->test_items[index];
        if (!(strcmp(item->last_status, "skipped") == 0 || strcmp(item->last_status, "SKIPPED") == 0)) continue;
        used += (size_t)snprintf(buffer + used, buffer_size - used, "%s\"", first_skipped ? "" : ",");
        append_json_escaped(buffer, buffer_size, &used, item->test_id);
        used += (size_t)snprintf(buffer + used, buffer_size - used, "\"");
        first_skipped = 0;
    }
    used += (size_t)snprintf(buffer + used, buffer_size - used, "]}");
    return (int)used;
}

int board_state_record_session_result(const char *path, const char *session_id,
                                      const char *start_time, const char *end_time,
                                      const char *verdict)
{
    struct board_state state;

    if (path == NULL || verdict == NULL) return -1;
    board_state_load_from_file(path, &state);
    copy_text(state.last_session_id, sizeof(state.last_session_id), session_id);
    copy_text(state.last_start_time, sizeof(state.last_start_time), start_time);
    copy_text(state.last_end_time, sizeof(state.last_end_time), end_time);
    copy_text(state.last_verdict, sizeof(state.last_verdict), verdict);
    copy_text(state.current_state, sizeof(state.current_state), "idle");
    copy_text(state.test_mode, sizeof(state.test_mode), "ready");
    if (strcmp(verdict, "Pass") == 0 || strcmp(verdict, "passed") == 0) state.pass_count++;
    else state.fail_count++;
    return board_state_save_to_file(path, &state);
}

int board_state_update_last_result(const char *path, const char *session_id,
                                   const char *start_time, const char *end_time,
                                   const char *verdict)
{
    struct board_state state;

    if (path == NULL || verdict == NULL) return -1;
    board_state_load_from_file(path, &state);
    copy_text(state.last_session_id, sizeof(state.last_session_id), session_id);
    copy_text(state.last_start_time, sizeof(state.last_start_time), start_time);
    copy_text(state.last_end_time, sizeof(state.last_end_time), end_time);
    copy_text(state.last_verdict, sizeof(state.last_verdict), verdict);
    copy_text(state.current_state, sizeof(state.current_state), "idle");
    copy_text(state.test_mode, sizeof(state.test_mode), "ready");
    return board_state_save_to_file(path, &state);
}

int board_state_record_test_items(const char *path,
                                  const struct board_test_item_summary *items,
                                  int item_count)
{
    struct board_state state;
    int index;
    int target;

    if (path == NULL || items == NULL || item_count < 0) return -1;
    board_state_load_from_file(path, &state);
    for (index = 0; index < item_count; ++index) {
        const struct board_test_item_summary *source = &items[index];
        if (source->test_id[0] == '\0') continue;
        target = find_test_item_index(&state, source->test_id);
        if (target < 0) {
            if (state.test_item_count >= BOARD_TEST_ITEM_CAPACITY) continue;
            target = state.test_item_count++;
            memset(&state.test_items[target], 0, sizeof(state.test_items[target]));
            copy_text(state.test_items[target].test_id, sizeof(state.test_items[target].test_id), source->test_id);
        }
        copy_text(state.test_items[target].last_status, sizeof(state.test_items[target].last_status), source->last_status);
        state.test_items[target].test_count += source->test_count;
    }
    return board_state_save_to_file(path, &state);
}

int board_state_write_last_result_json(const char *path, const char *session_id,
                                       const char *verdict,
                                       const struct board_test_item_summary *items,
                                       int item_count)
{
    char last_result_path[256];
    char temp_path[272];
    char timestamp[40];
    FILE *file;
    int index;
    int passed_count = 0;
    int failed_count = 0;
    int skipped_count = 0;

    if (path == NULL || verdict == NULL) return -1;
    if (build_last_result_path(path, last_result_path, sizeof(last_result_path)) != 0) return -1;
    if (ensure_parent_directory(last_result_path) != 0) return -1;
    snprintf(temp_path, sizeof(temp_path), "%s.tmp", last_result_path);
    format_timestamp_now(timestamp, sizeof(timestamp));

    for (index = 0; index < item_count; ++index) {
        const struct board_test_item_summary *item = &items[index];
        if (strcmp(item->last_status, "passed") == 0 || strcmp(item->last_status, "PASS") == 0) passed_count++;
        else if (strcmp(item->last_status, "skipped") == 0 || strcmp(item->last_status, "SKIPPED") == 0) skipped_count++;
        else if (item->last_status[0] != '\0') failed_count++;
    }

    file = fopen(temp_path, "w");
    if (file == NULL) return -1;
    if (fprintf(file,
                "{\n"
                "  \"sessionId\": \"%s\",\n"
                "  \"finalVerdict\": \"%s\",\n"
                "  \"updatedAt\": \"%s\",\n"
                "  \"passedItemCount\": %d,\n"
                "  \"failedItemCount\": %d,\n"
                "  \"skippedItemCount\": %d,\n"
                "  \"testItems\": [\n",
                session_id == NULL ? "" : session_id,
                verdict,
                timestamp,
                passed_count,
                failed_count,
                skipped_count) < 0) {
        fclose(file);
        remove(temp_path);
        return -1;
    }

    for (index = 0; index < item_count; ++index) {
        const struct board_test_item_summary *item = &items[index];
        if (item->test_id[0] == '\0') continue;
        if (fprintf(file,
                    "    {\"testId\": \"%s\", \"status\": \"%s\", \"testCount\": %d}%s\n",
                    item->test_id,
                    item->last_status,
                    item->test_count,
                    index == item_count - 1 ? "" : ",") < 0) {
            fclose(file);
            remove(temp_path);
            return -1;
        }
    }

    if (fprintf(file, "  ]\n}\n") < 0) {
        fclose(file);
        remove(temp_path);
        return -1;
    }

    fclose(file);
    if (rename(temp_path, last_result_path) != 0) {
        remove(temp_path);
        return -1;
    }
    return 0;
}
