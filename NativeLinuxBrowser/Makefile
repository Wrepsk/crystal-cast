CC ?= cc
PREFIX ?= /usr/local
PACKAGES = wpe-webkit-2.0 wpe-platform-2.0 json-glib-1.0 gstreamer-1.0
CFLAGS += -std=c11 -O2 -g -Wall -Wextra -Werror -D_GNU_SOURCE $(shell pkg-config --cflags $(PACKAGES))
LDLIBS += $(shell pkg-config --libs $(PACKAGES)) -pthread

.PHONY: all check-deps install clean
all: check-deps build/crystalcast-browser-helper

check-deps:
	pkg-config --atleast-version=2.54 wpe-webkit-2.0
	pkg-config --exists $(PACKAGES)

build/crystalcast-browser-helper: helper.c bridge.c bridge.h
	mkdir -p build
	$(CC) $(CFLAGS) helper.c bridge.c -o $@ $(LDLIBS)

install: all
	install -Dm755 build/crystalcast-browser-helper $(DESTDIR)$(PREFIX)/bin/crystalcast-browser-helper

clean:
	rm -f build/crystalcast-browser-helper
