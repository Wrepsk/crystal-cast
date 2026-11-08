CC = x86_64-w64-mingw32-gcc
CFLAGS = -O2 -std=c11 -Wall -Wextra -Werror

build/wine-frame-consumer.exe: consumer.c
	mkdir -p build
	$(CC) $(CFLAGS) -municode $< -o $@ -ld3d11 -ldxgi -ldxguid -lws2_32 -static-libgcc
