#define _POSIX_C_SOURCE 200809L

#include "config/app_config.h"
#include "manage/session_manager.h"

#include <arpa/inet.h>
#include <errno.h>
#include <netinet/in.h>
#include <pthread.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <unistd.h>

struct client_context {
    int client_fd;
    const struct app_config *config;
};

static pthread_mutex_t active_client_mutex = PTHREAD_MUTEX_INITIALIZER;
static int active_client_fd = -1;

static void *handle_client(void *argument)
{
    struct client_context *context = argument;
    int client_fd = context->client_fd;
    const struct app_config *config = context->config;
    free(context);
    session_manager_handle_client(client_fd, config);
    fprintf(stderr, "[SESSION] client closing fd=%d\n", client_fd);
    pthread_mutex_lock(&active_client_mutex);
    if (active_client_fd == client_fd) active_client_fd = -1;
    pthread_mutex_unlock(&active_client_mutex);
    close(client_fd);
    return NULL;
}

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
    if (listen(fd, 8) != 0) {
        close(fd);
        return -1;
    }
    return fd;
}

int main(void)
{
    struct app_config config;
    int listener;
    setvbuf(stdout, NULL, _IOLBF, 0);
    setvbuf(stderr, NULL, _IONBF, 0);
    app_config_load_defaults(&config);
    listener = create_listener(&config);
    if (listener < 0) {
        perror("create_listener");
        return 1;
    }
    printf("spaceTest3576 listening on %s:%d\n", config.bind_address, config.port);
    for (;;) {
        int client = accept(listener, NULL, NULL);
        struct client_context *context;
        pthread_t thread;
        if (client < 0) {
            perror("accept");
            continue;
        }
        fprintf(stderr, "[SESSION] client accepted fd=%d\n", client);
        pthread_mutex_lock(&active_client_mutex);
        if (active_client_fd >= 0) {
            fprintf(stderr, "[SESSION] superseding active client fd=%d with fd=%d\n", active_client_fd, client);
            shutdown(active_client_fd, SHUT_RDWR);
        }
        active_client_fd = client;
        pthread_mutex_unlock(&active_client_mutex);

        context = malloc(sizeof(*context));
        if (context == NULL) {
            close(client);
            continue;
        }
        context->client_fd = client;
        context->config = &config;
        if (pthread_create(&thread, NULL, handle_client, context) != 0) {
            free(context);
            close(client);
            continue;
        }
        pthread_detach(thread);
    }
}
