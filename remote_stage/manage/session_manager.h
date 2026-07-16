#ifndef SPACETEST3576_SESSION_MANAGER_H
#define SPACETEST3576_SESSION_MANAGER_H

#include "../config/app_config.h"

int session_manager_handle_client(int client_fd, const struct app_config *config);

#endif
