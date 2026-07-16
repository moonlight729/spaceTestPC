#define _POSIX_C_SOURCE 200809L

#include "config/app_config.h"
#include "manage/session_manager.h"

#include <arpa/inet.h>
#include <errno.h>
#include <netinet/in.h>
#include <stdio.h>
#include <string.h>
#include <sys/socket.h>
#include <unistd.h>

static int create_listener(const struct app_config *config)
{
    int fd;
    int enabled = 1;
    struct sockaddr_in address;
    fd = socket(AF_INET, SOCK_STREAM, 0);
    if (fd < 0) return -1;
    setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &enabled, sizeof(enabled));
    memset(&address, 0, sizeof(address));
    address.sin_family = AF_INET;
    address.sin_port = htons((uint16_t)config->port);
    if (inet_pton(AF_INET, config->bind_address, &address.sin_addr) != 1) {
        close(fd);
        errno = EINVAL;
        return -1;
    }
    if (bind(fd, (struct sockaddr *)&address, sizeof(address)) != 0) {
        close(fd);
        return -1;
    }
    if (listen(fd, 1) != 0) {
        close(fd);
        return -1;
    }
    return fd;
}

int main(void)
{
    struct app_config config;
    int listener;
    app_config_load_defaults(&config);
    listener = create_listener(&config);
    if (listener < 0) {
        perror("create_listener");
        return 1;
    }
    printf("spaceTest3576 listening on %s:%d\n", config.bind_address, config.port);
    for (;;) {
        int client = accept(listener, NULL, NULL);
        if (client < 0) {
            perror("accept");
            continue;
        }
        session_manager_handle_client(client, &config);
        close(client);
    }
}
