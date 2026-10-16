/* Standalone Windows PE harness for Wine's native-process and mapped-frame
 * bridge. No .NET/Dalamud/FFXIV dependency. Never disables WebKit's sandbox. */
#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static SOCKET peer = INVALID_SOCKET;
static int exact(void *data, unsigned length, int writing)
{
    unsigned position = 0;
    while (position < length) {
        int count = writing ? send(peer, (char *)data + position, length - position, 0)
                            : recv(peer, (char *)data + position, length - position, 0);
        if (count <= 0) return 0;
        position += count;
    }
    return 1;
}
static int message(const char *json)
{
    uint32_t header[] = { 0x31424343, 1, (uint32_t)strlen(json), 0 };
    return exact(header, sizeof header, 1) && exact((void *)json, header[2], 1);
}
static long long number(const char *json, const char *name)
{
    char key[64]; snprintf(key, sizeof key, "\"%s\":", name);
    const char *value = strstr(json, key);
    return value ? strtoll(value + strlen(key), NULL, 10) : -1;
}
static int fail(const char *reason) { fprintf(stderr, "FAIL: %s (%lu)\n", reason, GetLastError()); return 1; }

int main(int argc, char **argv)
{
    if (argc != 2 || argv[1][0] != '/' || strchr(argv[1], '"')) return fail("Pass the absolute native helper/launcher path");
    char runtime[1024];
    if (!GetEnvironmentVariableA("XDG_RUNTIME_DIR", runtime, sizeof runtime) || runtime[0] != '/') return fail("Missing desktop runtime root");
    char session[33];
    snprintf(session, sizeof session, "%016llx%016llx", (unsigned long long)GetTickCount64(), (unsigned long long)GetCurrentProcessId());
    char native[1400], endpoint[1500], frames[1500], command[8192];
    snprintf(native, sizeof native, "%s/crystalcast/%s", runtime, session);
    snprintf(endpoint, sizeof endpoint, "Z:%s/endpoint.json", native);
    snprintf(frames, sizeof frames, "Z:%s/frames.bin", native);
    for (unsigned i = 0; endpoint[i]; i++) if (endpoint[i] == '/') endpoint[i] = '\\';
    for (unsigned i = 0; frames[i]; i++) if (frames[i] == '/') frames[i] = '\\';
    snprintf(command, sizeof command, "start.exe /wait /unix \"%s\" --session %s", argv[1], session);
    STARTUPINFOA startup = { .cb = sizeof startup };
    PROCESS_INFORMATION process = {0};
    char ack[1500];
    snprintf(ack, sizeof ack, "Z:%s/permissions with spaces.ack", runtime);
    for (unsigned i = 0; ack[i]; i++) if (ack[i] == '/') ack[i] = '\\';
    snprintf(command, sizeof command,
        "start.exe /wait /unix /bin/sh -c \"ack=$1; shift; /bin/chmod \\\"$@\\\" && printf ok > \\\"$ack\\\"\" crystalcast-permissions \"%s/permissions with spaces.ack\" u+x -- \"%s\"", runtime, argv[1]);
    if (!CreateProcessA(NULL, command, NULL, NULL, FALSE, CREATE_NO_WINDOW, NULL, NULL, &startup, &process)) return fail("Native permission command");
    CloseHandle(process.hThread); CloseHandle(process.hProcess);
    for (unsigned attempt = 0; attempt < 300 && GetFileAttributesA(ack) == INVALID_FILE_ATTRIBUTES; attempt++) Sleep(50);
    if (GetFileAttributesA(ack) == INVALID_FILE_ATTRIBUTES) return fail("Native permission acknowledgement");
    DeleteFileA(ack);
    snprintf(command, sizeof command, "start.exe /wait /unix \"%s\" --session %s", argv[1], session);
    if (!CreateProcessA(NULL, command, NULL, NULL, FALSE, CREATE_NO_WINDOW, NULL, NULL, &startup, &process)) return fail("Wine native launch");
    CloseHandle(process.hThread);
    char description[4096] = {0};
    HANDLE file = INVALID_HANDLE_VALUE;
    for (unsigned attempt = 0; attempt < 300; attempt++) {
        file = CreateFileA(endpoint, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL, OPEN_EXISTING, 0, NULL);
        if (file != INVALID_HANDLE_VALUE) break;
        Sleep(50);
    }
    if (file == INVALID_HANDLE_VALUE) return fail("Endpoint not visible through Wine Z drive");
    DWORD read;
    if (!ReadFile(file, description, sizeof description - 1, &read, NULL)) return fail("Endpoint read");
    CloseHandle(file);
    long long helper_pid = number(description, "pid");
    if (helper_pid <= 0) return fail("Missing native helper PID");
    const char *token_start = strstr(description, "\"token\":\"");
    if (!token_start || strlen(token_start) < 75) return fail("Endpoint token format");
    char token[65]; memcpy(token, token_start + 9, 64); token[64] = 0;
    WSADATA sockets;
    if (WSAStartup(MAKEWORD(2, 2), &sockets)) return fail("WinSock initialization");
    peer = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    DWORD timeout = 10000;
    setsockopt(peer, SOL_SOCKET, SO_RCVTIMEO, (const char *)&timeout, sizeof timeout);
    struct sockaddr_in address = { .sin_family = AF_INET, .sin_port = htons((u_short)number(description, "port")) };
    address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    if (connect(peer, (struct sockaddr *)&address, sizeof address)) return fail("Wine to native loopback");
    snprintf(command, sizeof command, "{\"type\":\"hello\",\"version\":1,\"token\":\"%s\",\"transport\":\"shared-file\"}", token);
    if (!message(command)) return fail("Authentication write");
    file = CreateFileA(frames, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL, OPEN_EXISTING, 0, NULL);
    HANDLE mapping = CreateFileMappingA(file, NULL, PAGE_READONLY, 0, 0, NULL);
    const unsigned char *pixels = MapViewOfFile(mapping, FILE_MAP_READ, 0, 0, 0);
    if (!pixels) return fail("Wine native shared-frame mapping");
    if (!message("{\"type\":\"probe\"}") || !message(
        "{\"type\":\"load\",\"uri\":\"https://crystalcast.local/player/wine-probe.html\",\"width\":640,\"height\":360,\"fps\":30,\"generic\":false,\"html\":\"<canvas id='c'></canvas><script>const c=document.querySelector('canvas');c.width=640;c.height=360;let n=0;function f(){let x=c.getContext('2d');x.fillStyle=n++%2?'red':'blue';x.fillRect(0,0,640,360);requestAnimationFrame(f)}f();window.chrome.webview.postMessage({type:'probe',value:42});</script>\"}")) return fail("Probe/load command");
    unsigned captured = 0, changed = 0, ready = 0, javascript = 0;
    uint32_t previous = 0;
    ULONGLONG started = GetTickCount64();
    while (GetTickCount64() - started < 5000) {
        uint32_t header[4];
        if (!exact(header, sizeof header, 0) || header[0] != 0x31424343 || header[1] != 1 || header[2] > 524288) return fail("Protocol header");
        char *json = calloc(header[2] + 1, 1);
        if (!json || !exact(json, header[2], 0)) return fail("Protocol payload");
        if (strstr(json, "\"type\":\"probe\"")) ready++;
        if (strstr(json, "\\\"value\\\":42")) javascript++;
        if (strstr(json, "\"type\":\"frame\"")) {
            long long slot = number(json, "slot"), sequence = number(json, "sequence");
            if (slot < 0 || slot >= 3 || number(json, "width") != 640 || number(json, "height") != 360) return fail("Frame bounds");
            uint32_t first;
            memcpy(&first, pixels + slot * 1920 * 1080 * 4 + (180 * 640 + 320) * 4, sizeof first);
            if (captured && first != previous) changed++;
            previous = first; captured++;
            snprintf(command, sizeof command, "{\"type\":\"ack\",\"slot\":%lld,\"sequence\":%lld}", slot, sequence);
            if (!message(command)) return fail("Frame acknowledgement");
        }
        free(json);
        if (!message("{\"type\":\"ping\"}")) return fail("Heartbeat");
    }
    message("{\"type\":\"shutdown\"}");
    closesocket(peer); UnmapViewOfFile(pixels); CloseHandle(mapping); CloseHandle(file);
    CloseHandle(process.hProcess);
    for (unsigned attempt = 0; attempt < 200 && GetFileAttributesA(endpoint) != INVALID_FILE_ATTRIBUTES; attempt++) Sleep(50);
    char proc[80]; snprintf(proc, sizeof proc, "Z:\\proc\\%lld", helper_pid);
    for (unsigned attempt = 0; attempt < 200 && GetFileAttributesA(proc) != INVALID_FILE_ATTRIBUTES; attempt++) Sleep(50);
    if (GetFileAttributesA(proc) != INVALID_FILE_ATTRIBUTES) return fail("Native helper process did not terminate");
    if (GetFileAttributesA(endpoint) != INVALID_FILE_ATTRIBUTES) return fail("Session resources were not cleaned");
    printf("frames=%u changes=%u engineReplies=%u jsMessages=%u; Wine launch, loopback, mapping and cleanup verified\n", captured, changed, ready, javascript);
    WSACleanup();
    return captured > 10 && changed > 2 && ready && javascript ? 0 : 1;
}
