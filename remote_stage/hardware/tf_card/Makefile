CC ?= gcc
CFLAGS ?= -std=c11 -Wall -Wextra -Werror -O2

all: tf_card.o tf_card_test
tf_card.o: tf_card.c tf_card.h
	$(CC) $(CFLAGS) -c tf_card.c -o $@
tf_card_test: tf_card_test.c tf_card.o tf_card.h
	$(CC) $(CFLAGS) tf_card_test.c tf_card.o -o $@
clean:
	rm -f tf_card.o tf_card_test
