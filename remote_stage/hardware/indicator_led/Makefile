CC ?= gcc
CFLAGS ?= -std=c11 -Wall -Wextra -Werror -O2

all: indicator_led.o indicator_led_test
indicator_led.o: indicator_led.c indicator_led.h
	$(CC) $(CFLAGS) -c indicator_led.c -o $@
indicator_led_test: indicator_led_test.c indicator_led.o indicator_led.h
	$(CC) $(CFLAGS) indicator_led_test.c indicator_led.o -o $@
clean:
	rm -f indicator_led.o indicator_led_test
