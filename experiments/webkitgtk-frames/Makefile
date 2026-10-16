CC = cc
PKGS = webkitgtk-6.0 gtk4 epoxy gstreamer-1.0
CFLAGS = -O2 -g -std=c11 -Wall -Wextra -Werror

build/gtk-frame-probe: probe.c
	mkdir -p build
	$(CC) $(CFLAGS) $< -o $@ $$(pkg-config --cflags --libs $(PKGS))

.PHONY: clean
clean:
	rm -f build/gtk-frame-probe
