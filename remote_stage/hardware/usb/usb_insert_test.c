#define _POSIX_C_SOURCE 200809L

#include "usb_insert_test.h"

#include <ctype.h>
#include <dirent.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static int is_usb_topology_name(const char *name)
{
    const unsigned char *cursor = (const unsigned char *)name;
    int has_dash = 0;
    if (name == NULL || !isdigit(*cursor)) return 0;
    while (*cursor != '\0') {
        if (*cursor == ':') return 0;
        if (*cursor == '-') has_dash = 1;
        else if (!isdigit(*cursor) && *cursor != '.') return 0;
        cursor++;
    }
    return has_dash;
}

static void extract_topology(const char *resolved_path, char *topology, size_t topology_size)
{
    char *path;
    char *save = NULL;
    char *part;
    if (topology == NULL || topology_size == 0) return;
    topology[0] = '\0';
    path = strdup(resolved_path == NULL ? "" : resolved_path);
    if (path == NULL) return;
    part = strtok_r(path, "/", &save);
    while (part != NULL) {
        if (is_usb_topology_name(part)) snprintf(topology, topology_size, "%s", part);
        part = strtok_r(NULL, "/", &save);
    }
    free(path);
}

static int read_speed_from_parents(const char *resolved_path, int *speed_mbps)
{
    char *path;
    if (resolved_path == NULL || speed_mbps == NULL) return -1;
    path = strdup(resolved_path);
    if (path == NULL) return -1;
    for (;;) {
        char *speed_path;
        FILE *file;
        char *slash;
        size_t speed_path_size = strlen(path) + sizeof("/speed");
        speed_path = malloc(speed_path_size);
        if (speed_path == NULL) {
            free(path);
            return -1;
        }
        snprintf(speed_path, speed_path_size, "%s/speed", path);
        file = fopen(speed_path, "r");
        free(speed_path);
        if (file != NULL) {
            int speed;
            int rc = fscanf(file, "%d", &speed);
            fclose(file);
            if (rc == 1 && speed > 0) {
                *speed_mbps = speed;
                free(path);
                return 0;
            }
        }
        slash = strrchr(path, '/');
        if (slash == NULL || slash == path) break;
        *slash = '\0';
    }
    free(path);
    return -1;
}

int usb_insert_find(const char *topology_filter, struct usb_insert_device *device)
{
    DIR *directory;
    struct dirent *entry;
    if (device == NULL) return -1;
    memset(device, 0, sizeof(*device));
    directory = opendir("/sys/block");
    if (directory == NULL) return -1;
    while ((entry = readdir(directory)) != NULL) {
        char device_link[512];
        char *resolved;
        char topology[64];
        int speed_mbps;
        size_t block_name_length;
        if (entry->d_name[0] != 's' || entry->d_name[1] != 'd' || entry->d_name[2] == '\0') continue;
        block_name_length = strnlen(entry->d_name, sizeof(device->block_name));
        if (block_name_length >= sizeof(device->block_name)) continue;
        snprintf(device_link, sizeof(device_link), "/sys/block/%s/device", entry->d_name);
        resolved = realpath(device_link, NULL);
        if (resolved == NULL) continue;
        extract_topology(resolved, topology, sizeof(topology));
        if (topology_filter != NULL && topology_filter[0] != '\0' &&
            strcmp(topology, topology_filter) != 0) {
            free(resolved);
            continue;
        }
        if (read_speed_from_parents(resolved, &speed_mbps) != 0) {
            free(resolved);
            continue;
        }
        free(resolved);
        memcpy(device->block_name, entry->d_name, block_name_length + 1);
        snprintf(device->topology, sizeof(device->topology), "%s", topology);
        device->speed_mbps = speed_mbps;
        closedir(directory);
        return 1;
    }
    closedir(directory);
    return 0;
}
