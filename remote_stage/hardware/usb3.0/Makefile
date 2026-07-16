CC ?= gcc
CFLAGS ?= -std=c11 -Wall -Wextra -Werror -O2

all: usb3_file_check.o usb3_test
usb3_file_check.o: usb3_file_check.c usb3_file_check.h
	$(CC) $(CFLAGS) -c usb3_file_check.c -o $@
usb3_test: usb3_test.c usb3_file_check.o usb3_file_check.h
	$(CC) $(CFLAGS) usb3_test.c usb3_file_check.o -o $@
clean:
	rm -f usb3_file_check.o usb3_test
