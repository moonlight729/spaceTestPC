#define _POSIX_C_SOURCE 200809L
#include "tf_card.h"

#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <sys/statvfs.h>
#include <unistd.h>

static void copy_text(char *dst, size_t dst_size, const char *src)
{
    size_t length;
    if (dst_size == 0) return;
    if (src == NULL) src = "";
    length = strnlen(src, dst_size - 1);
    memcpy(dst, src, length);
    dst[length] = '\0';
}

static void set_message(struct tf_card_result *result, int code, const char *message)
{
    result->error_code = code;
    copy_text(result->message, sizeof(result->message), message);
}

static int run_command(const char *command)
{
    int rc = system(command);
    if (rc != 0) errno = EIO;
    return rc == 0 ? 0 : -1;
}

static int read_command_line(const char *command, char *buffer, size_t buffer_size)
{
    FILE *pipe;
    char *newline;
    if (buffer == NULL || buffer_size == 0) {
        errno = EINVAL;
        return -1;
    }
    buffer[0] = '\0';
    pipe = popen(command, "r");
    if (pipe == NULL) return -1;
    if (fgets(buffer, (int)buffer_size, pipe) == NULL) {
        pclose(pipe);
        errno = ENODATA;
        return -1;
    }
    if (pclose(pipe) != 0) return -1;
    newline = strchr(buffer, '\n');
    if (newline != NULL) *newline = '\0';
    return 0;
}

static int path_exists(const char *path)
{
    struct stat st;
    return path != NULL && stat(path, &st) == 0;
}

static int ensure_directory(const char *path)
{
    if (path_exists(path)) return 0;
    return mkdir(path, 0755);
}

static int read_filesystem(const char *device_path, char *filesystem, size_t filesystem_size)
{
    char command[256];
    snprintf(command, sizeof(command), "blkid -o value -s TYPE '%s' 2>/dev/null", device_path);
    if (read_command_line(command, filesystem, filesystem_size) != 0) {
        filesystem[0] = '\0';
        return -1;
    }
    return 0;
}

static int read_mount_point(const char *device_path, char *mount_point, size_t mount_point_size)
{
    char command[256];
    snprintf(command, sizeof(command), "findmnt -nr -S '%s' -o TARGET 2>/dev/null", device_path);
    return read_command_line(command, mount_point, mount_point_size);
}

static int format_ext4(const char *device_path)
{
    char command[256];
    snprintf(command, sizeof(command), "mkfs.ext4 -F '%s' >/dev/null 2>&1", device_path);
    return run_command(command);
}

static int mount_device(const char *device_path, const char *mount_point)
{
    char command[320];
    snprintf(command, sizeof(command), "mount '%s' '%s'", device_path, mount_point);
    return run_command(command);
}

static int update_capacity(struct tf_card_result *result)
{
    struct statvfs vfs;
    if (statvfs(result->mount_point, &vfs) != 0) return -1;
    result->total_mb = (uint64_t)vfs.f_blocks * (uint64_t)vfs.f_frsize / 1024U / 1024U;
    result->free_mb = (uint64_t)vfs.f_bavail * (uint64_t)vfs.f_frsize / 1024U / 1024U;
    return 0;
}

static int rw_check(const char *mount_point)
{
    char path[256];
    FILE *file;
    char buffer[32];
    snprintf(path, sizeof(path), "%s/.spacetest_tf_check", mount_point);
    file = fopen(path, "w");
    if (file == NULL) return -1;
    if (fputs("spacetest_tf_card\n", file) < 0) {
        fclose(file);
        return -1;
    }
    fclose(file);
    file = fopen(path, "r");
    if (file == NULL) return -1;
    if (fgets(buffer, sizeof(buffer), file) == NULL) {
        fclose(file);
        return -1;
    }
    fclose(file);
    remove(path);
    return strcmp(buffer, "spacetest_tf_card\n") == 0 ? 0 : -1;
}

int tf_card_run_test(const struct tf_card_request *request,
                     struct tf_card_result *result)
{
    char active_mount[160];

    if (request == NULL || result == NULL || request->device_path == NULL ||
        request->mount_point == NULL) {
        errno = EINVAL;
        return -1;
    }
    memset(result, 0, sizeof(*result));
    copy_text(result->device_path, sizeof(result->device_path), request->device_path);
    copy_text(result->mount_point, sizeof(result->mount_point), request->mount_point);

    if (!path_exists(request->device_path)) {
        set_message(result, 4300, "TF card device is not present");
        return -1;
    }
    result->present = true;

    if (read_filesystem(request->device_path, result->filesystem, sizeof(result->filesystem)) != 0 ||
        strcmp(result->filesystem, "ext4") != 0) {
        if (!request->allow_format_ext4) {
            set_message(result, 4301, "TF card filesystem is not ext4 and formatting is disabled");
            return -1;
        }
        if (format_ext4(request->device_path) != 0 ||
            read_filesystem(request->device_path, result->filesystem, sizeof(result->filesystem)) != 0) {
            set_message(result, 4302, "Unable to format TF card as ext4");
            return -1;
        }
        result->formatted = true;
    }

    if (ensure_directory(request->mount_point) != 0) {
        set_message(result, 4303, "Unable to create TF card mount point");
        return -1;
    }

    if (read_mount_point(request->device_path, active_mount, sizeof(active_mount)) == 0) {
        copy_text(result->mount_point, sizeof(result->mount_point), active_mount);
        result->mounted = true;
    } else {
        if (mount_device(request->device_path, request->mount_point) != 0) {
            set_message(result, 4304, "Unable to mount TF card");
            return -1;
        }
        result->mounted = true;
    }

    if (update_capacity(result) != 0) {
        set_message(result, 4305, "Unable to read TF card capacity");
        return -1;
    }
    if (request->min_capacity_mb > 0 && result->total_mb < (uint64_t)request->min_capacity_mb) {
        set_message(result, 4306, "TF card capacity is below the configured limit");
        return -1;
    }
    if (rw_check(result->mount_point) != 0) {
        set_message(result, 4307, "TF card read/write check failed");
        return -1;
    }

    result->rw_passed = true;
    set_message(result, 0, "TF card check passed");
    return 0;
}
