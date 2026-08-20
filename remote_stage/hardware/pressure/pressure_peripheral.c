#define _POSIX_C_SOURCE 200809L
#include "pressure_peripheral.h"

#include <dirent.h>
#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <unistd.h>
#include <sys/stat.h>

static int write_value(const char *path, int value)
{
    FILE *file = fopen(path, "w");
    if (file == NULL) return -1;
    if (fprintf(file, "%d\n", value) < 0) { fclose(file); return -1; }
    return fclose(file) == 0 ? 0 : -1;
}

int pressure_check_fan(const char *pwm_path, const char *tach_path, struct pressure_peripheral_result *result)
{
    FILE *file;
    struct timespec delay = { .tv_sec = 1, .tv_nsec = 0 };
    memset(result, 0, sizeof(*result));
    if (write_value(pwm_path, 100) != 0) { result->error_code = 6401; snprintf(result->detail, sizeof(result->detail), "Unable to start fan PWM"); return -1; }
    nanosleep(&delay, NULL);
    file = fopen(tach_path, "r");
    if (file == NULL || fscanf(file, "%d", &result->value) != 1) {
        if (file != NULL) fclose(file);
        (void)write_value(pwm_path, 0);
        result->error_code = 6402;
        snprintf(result->detail, sizeof(result->detail), "Unable to read fan tach_rpm");
        return -1;
    }
    fclose(file);
    if (write_value(pwm_path, 0) != 0) { result->error_code = 6403; snprintf(result->detail, sizeof(result->detail), "Unable to stop fan PWM"); return -1; }
    result->active = result->value > 0;
    snprintf(result->detail, sizeof(result->detail), "tach_rpm=%d", result->value);
    if (!result->active) { result->error_code = 6404; return -1; }
    return 0;
}

int pressure_check_hdmi(struct pressure_peripheral_result *result)
{
    DIR *directory;
    struct dirent *entry;
    char path[512];
    char status[32];
    memset(result, 0, sizeof(*result));
    directory = opendir("/sys/class/drm");
    if (directory == NULL) { result->error_code = 6501; snprintf(result->detail, sizeof(result->detail), "DRM sysfs unavailable"); return -1; }
    while ((entry = readdir(directory)) != NULL) {
        if (strstr(entry->d_name, "HDMI") == NULL) continue;
        snprintf(path, sizeof(path), "/sys/class/drm/%.200s/status", entry->d_name);
        FILE *file = fopen(path, "r");
        if (file == NULL) continue;
        status[0] = '\0';
        if (fgets(status, sizeof(status), file) == NULL) status[0] = '\0';
        fclose(file);
        result->value++;
        if (strncmp(status, "connected", 9) == 0) result->active = 1;
    }
    closedir(directory);
    snprintf(result->detail, sizeof(result->detail), "hdmi_connectors=%d", result->value);
    if (result->value == 0) { result->error_code = 6502; return -1; }
    return 0;
}

static int is_usb_block_source(const char *source)
{
    char block_name[128];
    char sys_path[256];
    char resolved[512];
    if (strncmp(source, "/dev/sd", 7) != 0) return 0;
    if (snprintf(block_name, sizeof(block_name), "%s", source + 5) >= (int)sizeof(block_name)) return 0;
    snprintf(sys_path, sizeof(sys_path), "/sys/class/block/%s", block_name);
    return realpath(sys_path, resolved) != NULL && strstr(resolved, "/usb") != NULL;
}

static int pressure_check_usb_mount(const char *mount_point, int index, char *detail, size_t detail_size)
{
    char directory[320];
    char file_path[384];
    unsigned char write_buffer[4096];
    unsigned char read_buffer[4096];
    FILE *file;
    int block;
    snprintf(directory, sizeof(directory), "%s/.spacetest-pressure", mount_point);
    if (mkdir(directory, 0700) != 0 && errno != EEXIST) { snprintf(detail, detail_size, "USB%d cannot create test directory at %s", index, mount_point); return -1; }
    snprintf(file_path, sizeof(file_path), "%s/usb-rw-%d.bin", directory, index);
    file = fopen(file_path, "w+b");
    if (file == NULL) { snprintf(detail, detail_size, "USB%d cannot create test file at %s", index, mount_point); return -1; }
    for (block = 0; block < 1024; ++block) {
        memset(write_buffer, block & 0xff, sizeof(write_buffer));
        if (fwrite(write_buffer, 1, sizeof(write_buffer), file) != sizeof(write_buffer)) goto io_error;
    }
    if (fflush(file) != 0 || fsync(fileno(file)) != 0 || fseek(file, 0, SEEK_SET) != 0) goto io_error;
    for (block = 0; block < 1024; ++block) {
        memset(write_buffer, block & 0xff, sizeof(write_buffer));
        if (fread(read_buffer, 1, sizeof(read_buffer), file) != sizeof(read_buffer) || memcmp(write_buffer, read_buffer, sizeof(write_buffer)) != 0) goto io_error;
    }
    fclose(file);
    (void)unlink(file_path);
    snprintf(detail, detail_size, "USB%d 4MiB write/read passed at %.80s", index, mount_point);
    return 0;
io_error:
    fclose(file);
    (void)unlink(file_path);
    snprintf(detail, detail_size, "USB%d write/read verify failed at %.80s", index, mount_point);
    return -1;
}

int pressure_check_usb_storage(struct pressure_peripheral_result *result)
{
    char source[128]; char mount[256]; char fs[64]; char detail[256];
    FILE *mounts;
    memset(result, 0, sizeof(*result));
    mounts = fopen("/proc/mounts", "r");
    if (mounts == NULL) { result->error_code = 6601; snprintf(result->detail, sizeof(result->detail), "Unable to enumerate mounted USB storage"); return -1; }
    while (fscanf(mounts, "%127s %255s %63s %*s %*d %*d", source, mount, fs) == 3) {
        if (!is_usb_block_source(source) || strcmp(mount, "/") == 0 || access(mount, W_OK) != 0) continue;
        fclose(mounts);
        if (pressure_check_usb_mount(mount, 1, detail, sizeof(detail)) != 0) {
            result->error_code = 6604;
            snprintf(result->detail, sizeof(result->detail), "%.127s", detail);
            return -1;
        }
        result->active = 1;
        result->value = 1;
        snprintf(result->detail, sizeof(result->detail), "%.127s", detail);
        return 0;
    }
    fclose(mounts);
    result->error_code = 6601;
    snprintf(result->detail, sizeof(result->detail), "No writable mounted USB mass-storage partition");
    return -1;
}

int pressure_check_file_storage(const char *directory, const char *label, struct pressure_peripheral_result *result)
{
    char file_path[384];
    unsigned char write_buffer[4096];
    unsigned char read_buffer[4096];
    FILE *file;
    int block;
    memset(result, 0, sizeof(*result));
    if (directory == NULL || access(directory, W_OK) != 0) { result->error_code = 6701; snprintf(result->detail, sizeof(result->detail), "%s test directory unavailable", label); return -1; }
    snprintf(file_path, sizeof(file_path), "%.300s/.spacetest-pressure-rw.bin", directory);
    file = fopen(file_path, "w+b");
    if (file == NULL) { result->error_code = 6702; snprintf(result->detail, sizeof(result->detail), "%s test file open failed", label); return -1; }
    for (block = 0; block < 1024; ++block) { memset(write_buffer, block & 0xff, sizeof(write_buffer)); if (fwrite(write_buffer, 1, sizeof(write_buffer), file) != sizeof(write_buffer)) goto storage_error; }
    if (fflush(file) != 0 || fseek(file, 0, SEEK_SET) != 0) goto storage_error;
    for (block = 0; block < 1024; ++block) { memset(write_buffer, block & 0xff, sizeof(write_buffer)); if (fread(read_buffer, 1, sizeof(read_buffer), file) != sizeof(read_buffer) || memcmp(write_buffer, read_buffer, sizeof(write_buffer)) != 0) goto storage_error; }
    fclose(file); (void)unlink(file_path);
    result->active = 1; result->value = 4;
    snprintf(result->detail, sizeof(result->detail), "%s 4MiB write/read verify passed", label);
    return 0;
storage_error:
    fclose(file); (void)unlink(file_path);
    result->error_code = 6703; snprintf(result->detail, sizeof(result->detail), "%s write/read verify failed", label);
    return -1;
}
