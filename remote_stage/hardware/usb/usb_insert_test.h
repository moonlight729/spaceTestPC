#ifndef SPACETEST3576_USB_INSERT_TEST_H
#define SPACETEST3576_USB_INSERT_TEST_H

#include <stddef.h>

struct usb_insert_device {
    char block_name[32];
    char topology[64];
    int speed_mbps;
};

int usb_insert_find(const char *topology_filter, struct usb_insert_device *device);

#endif
