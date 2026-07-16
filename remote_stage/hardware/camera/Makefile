CC ?= gcc
CFLAGS ?= -std=c11 -Wall -Wextra -Werror -O2

all: camera_stream.o camera_test
camera_stream.o: camera_stream.c camera_stream.h
	$(CC) $(CFLAGS) -c camera_stream.c -o $@
camera_test: camera_test.c camera_stream.o camera_stream.h
	$(CC) $(CFLAGS) camera_test.c camera_stream.o -o $@
clean:
	rm -f camera_stream.o camera_test
