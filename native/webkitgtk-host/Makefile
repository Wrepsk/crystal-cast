CFLAGS ?= -O2 -g -Wall -Wextra -Werror
PKGS = webkitgtk-6.0 gtk4 gtk4-x11 x11

build/crystalcast-webkit-host: host.c
	mkdir -p build
	$(CC) $(CFLAGS) -o $@ host.c $$(pkg-config --cflags --libs $(PKGS))
