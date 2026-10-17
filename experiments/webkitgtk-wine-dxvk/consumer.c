/* Standalone D3D11 consumer. No plugin, CLR, WebKit or GPU-sharing implementation. */
#define COBJMACROS
#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <windows.h>
#include <d3d11.h>
#include <dxgi.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

enum { WIDTH = 1920, HEIGHT = 1080, STRIDE = WIDTH * 4, SLOTS = 3 };
#define FRAME_BYTES ((size_t)STRIDE * HEIGHT)
typedef struct { uint64_t seq; uint32_t slot, bytes; int64_t producer_time; } Notice;
_Static_assert(sizeof(Notice) == 24, "protocol layout");
static LARGE_INTEGER frequency;
static BOOL closed;
static const char *phase = "initialization";

static LONG WINAPI crash(EXCEPTION_POINTERS *exception)
{
    fprintf(stderr, "Consumer exception 0x%08lx during %s\n",
            (unsigned long)exception->ExceptionRecord->ExceptionCode, phase);
    HMODULE module;
    void *address = exception->ExceptionRecord->ExceptionAddress;
    if (GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                          (LPCWSTR)address, &module)) {
        wchar_t name[MAX_PATH];
        if (GetModuleFileNameW(module, name, MAX_PATH)) {
            wchar_t *base = wcsrchr(name, L'\\');
            fprintf(stderr, "Exception module: %ls + 0x%llx\n", base ? base + 1 : name,
                    (unsigned long long)((uintptr_t)address - (uintptr_t)module));
        }
    }
    fflush(stderr);
    ExitProcess(1);
    return EXCEPTION_EXECUTE_HANDLER;
}

static double now_us(void)
{
    LARGE_INTEGER n;
    QueryPerformanceCounter(&n);
    return (double)n.QuadPart * 1000000.0 / (double)frequency.QuadPart;
}

static void pump(void)
{
    MSG message;
    while (PeekMessageW(&message, NULL, 0, 0, PM_REMOVE)) {
        if (message.message == WM_QUIT) closed = TRUE;
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }
}

static LRESULT CALLBACK window_proc(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam)
{
    if (message == WM_CLOSE) { closed = TRUE; return 0; }
    return DefWindowProcW(hwnd, message, wparam, lparam);
}

static BOOL transfer(SOCKET socket, void *data, int bytes, BOOL writing)
{
    char *p = data;
    double deadline = now_us() + 45000000.0;
    while (bytes > 0 && !closed && now_us() < deadline) {
        pump();
        fd_set set;
        FD_ZERO(&set);
        FD_SET(socket, &set);
        struct timeval timeout = { 0, 10000 };
        int ready = select(0, writing ? NULL : &set, writing ? &set : NULL, NULL, &timeout);
        if (ready == SOCKET_ERROR) return FALSE;
        if (!ready) continue;
        int size = writing ? send(socket, p, bytes, 0) : recv(socket, p, bytes, 0);
        if (size <= 0) return FALSE;
        p += size;
        bytes -= size;
    }
    return bytes == 0;
}

static void check(HRESULT result, const char *operation)
{
    if (FAILED(result)) {
        fprintf(stderr, "%s failed: HRESULT 0x%08lx\n", operation, (unsigned long)result);
        exit(1);
    }
}

static uint64_t sample_hash(const unsigned char *image, UINT pitch)
{
    uint64_t hash = UINT64_C(14695981039346656037);
    for (size_t offset = 0; offset < FRAME_BYTES; offset += 4096)
        hash = (hash ^ image[(offset / STRIDE) * pitch + offset % STRIDE]) * UINT64_C(1099511628211);
    return hash;
}

static int counter(const unsigned char *image, UINT pitch)
{
    int marker = 0;
    for (int bit = 0; bit < 10; ++bit)
        if (image[24 * pitch + (bit * 96 + 40) * 4] > 127) marker |= 1 << bit;
    return marker;
}

static BOOL bars(const unsigned char *image, UINT pitch)
{
    const unsigned char *r = image + 1040 * pitch + 80 * 4;
    const unsigned char *g = image + 1040 * pitch + 240 * 4;
    const unsigned char *b = image + 1040 * pitch + 400 * 4;
    return r[2] > 180 && r[0] < 40 && g[1] > 180 && g[0] < 40 &&
        b[0] > 180 && b[2] < 40 && r[3] == 255 && g[3] == 255 && b[3] == 255;
}

static BOOL overlay(const unsigned char *image, UINT pitch)
{
    const unsigned char *p = image + 810 * pitch + 1510 * 4;
    return p[0] == 255 && p[1] == 255 && p[2] == 0 && p[3] == 255;
}

int wmain(int argc, wchar_t **argv)
{
    if (argc != 5 || (wcscmp(argv[3], L"verify") && wcscmp(argv[3], L"upload"))) {
        fprintf(stderr, "Use the Linux run.py coordinator.\n"); return 2;
    }
    QueryPerformanceFrequency(&frequency);
    SetUnhandledExceptionFilter(crash);
    BOOL verify_all = !wcscmp(argv[3], L"verify"), require_overlay = !wcscmp(argv[4], L"overlay");
    char token[65];
    if (GetEnvironmentVariableA("CRYSTALCAST_BENCH_TOKEN", token, sizeof token) != 64) return 2;
    HANDLE file = CreateFileW(argv[1], GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                              NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    LARGE_INTEGER size;
    if (file == INVALID_HANDLE_VALUE || !GetFileSizeEx(file, &size) || size.QuadPart != (LONGLONG)(SLOTS * FRAME_BYTES)) {
        fprintf(stderr, "Cannot open the expected three-slot mapping through the Wine Z: drive.\n"); return 2;
    }
    HANDLE mapping = CreateFileMappingW(file, NULL, PAGE_READONLY, 0, 0, NULL);
    const unsigned char *pixels = mapping ? MapViewOfFile(mapping, FILE_MAP_READ, 0, 0, 0) : NULL;
    if (!pixels) { fprintf(stderr, "MapViewOfFile failed.\n"); return 2; }
    WNDCLASSW cls = {0};
    cls.lpfnWndProc = window_proc;
    cls.hInstance = GetModuleHandleW(NULL);
    cls.lpszClassName = L"CrystalCastWineFrameTest";
    if (!RegisterClassW(&cls)) return 2;
    RECT client = { 0, 0, 960, 540 };
    if (!AdjustWindowRect(&client, WS_OVERLAPPEDWINDOW, FALSE)) return 2;
    HWND hwnd = CreateWindowW(cls.lpszClassName, L"CrystalCast Wine/DXVK frame test", WS_OVERLAPPEDWINDOW,
                               CW_USEDEFAULT, CW_USEDEFAULT, client.right - client.left, client.bottom - client.top,
                               NULL, NULL, cls.hInstance, NULL);
    if (!hwnd) return 2;
    DXGI_SWAP_CHAIN_DESC swap = {0};
    swap.BufferDesc.Width = WIDTH;
    swap.BufferDesc.Height = HEIGHT;
    swap.BufferDesc.RefreshRate.Numerator = 60;
    swap.BufferDesc.RefreshRate.Denominator = 1;
    swap.BufferDesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    swap.SampleDesc.Count = 1;
    swap.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    swap.BufferCount = 2;
    swap.OutputWindow = hwnd;
    swap.Windowed = TRUE;
    swap.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;
    ID3D11Device *device = NULL;
    ID3D11DeviceContext *context = NULL;
    IDXGISwapChain *chain = NULL;
    D3D_FEATURE_LEVEL level;
    check(D3D11CreateDeviceAndSwapChain(NULL, D3D_DRIVER_TYPE_HARDWARE, NULL, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
        NULL, 0, D3D11_SDK_VERSION, &swap, &chain, &device, &level, &context), "D3D11CreateDeviceAndSwapChain");
    IDXGIDevice *dxgi = NULL;
    IDXGIAdapter *adapter = NULL;
    DXGI_ADAPTER_DESC description;
    check(ID3D11Device_QueryInterface(device, &IID_IDXGIDevice, (void **)&dxgi), "DXGI device");
    check(IDXGIDevice_GetAdapter(dxgi, &adapter), "DXGI adapter");
    check(IDXGIAdapter_GetDesc(adapter, &description), "DXGI description");
    char adapter_name[256];
    int i;
    for (i = 0; i < 255 && description.Description[i]; ++i) {
        wchar_t c = description.Description[i];
        adapter_name[i] = c >= 32 && c < 127 && c != '"' && c != '\\' ? (char)c : '?';
    }
    adapter_name[i] = 0;
    ID3D11Texture2D *backbuffer = NULL, *texture = NULL, *staging = NULL;
    check(IDXGISwapChain_GetBuffer(chain, 0, &IID_ID3D11Texture2D, (void **)&backbuffer), "backbuffer");
    D3D11_TEXTURE2D_DESC actual;
    ID3D11Texture2D_GetDesc(backbuffer, &actual);
    if (actual.Width != WIDTH || actual.Height != HEIGHT || actual.Format != DXGI_FORMAT_B8G8R8A8_UNORM) {
        fprintf(stderr, "Unexpected D3D11 backbuffer dimensions or format.\n"); return 2;
    }
    D3D11_TEXTURE2D_DESC desc = {0};
    desc.Width = WIDTH; desc.Height = HEIGHT; desc.MipLevels = 1; desc.ArraySize = 1;
    desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM; desc.SampleDesc.Count = 1;
    desc.Usage = D3D11_USAGE_DEFAULT; desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    check(ID3D11Device_CreateTexture2D(device, &desc, NULL, &texture), "upload texture");
    desc.Usage = D3D11_USAGE_STAGING; desc.BindFlags = 0; desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    check(ID3D11Device_CreateTexture2D(device, &desc, NULL, &staging), "verification staging texture");
    D3D11_QUERY_DESC query_desc = { D3D11_QUERY_EVENT, 0 };
    ID3D11Query *fence = NULL;
    check(ID3D11Device_CreateQuery(device, &query_desc, &fence), "completion query");
    WSADATA winsock;
    if (WSAStartup(MAKEWORD(2,2), &winsock)) return 2;
    SOCKET socket = WSASocketW(AF_INET, SOCK_STREAM, IPPROTO_TCP, NULL, 0, 0);
    struct sockaddr_in address = {0};
    int port = _wtoi(argv[2]);
    if (port < 1 || port > 65535 || socket == INVALID_SOCKET) return 2;
    address.sin_family = AF_INET; address.sin_port = htons((u_short)port);
    address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    if (connect(socket, (struct sockaddr *)&address, sizeof address) || !transfer(socket, token, 64, TRUE)) return 2;
    SecureZeroMemory(token, sizeof token);
    ShowWindow(hwnd, SW_SHOW);
    char line[2048];
    snprintf(line, sizeof line, "{\"kind\":\"consumer_start\",\"adapter\":\"%s\",\"feature_level\":%u,\"width\":%d,\"height\":%d}\n",
             adapter_name, (unsigned)level, WIDTH, HEIGHT);
    if (!transfer(socket, line, (int)strlen(line), TRUE)) return 2;
    uint64_t previous = 0;
    BOOL ok = TRUE;
    for (;;) {
        Notice notice;
        if (!transfer(socket, &notice, sizeof notice, FALSE)) { ok = FALSE; break; }
        if (!notice.seq) break;
        if (notice.seq <= previous || notice.slot >= SLOTS || notice.bytes != FRAME_BYTES) { ok = FALSE; break; }
        previous = notice.seq;
        double begin = now_us();
        phase = "source verification";
        const unsigned char *image = pixels + notice.slot * FRAME_BYTES;
        int marker = counter(image, STRIDE);
        uint64_t hash = sample_hash(image, STRIDE);
        BOOL source_valid = bars(image, STRIDE) && (!require_overlay || overlay(image, STRIDE));
        double source_end = now_us();
        phase = "UpdateSubresource";
        ID3D11DeviceContext_UpdateSubresource(context, (ID3D11Resource *)texture, 0, NULL, image, STRIDE, 0);
        double update_end = now_us();
        phase = "backbuffer copy and completion";
        ID3D11DeviceContext_CopyResource(context, (ID3D11Resource *)backbuffer, (ID3D11Resource *)texture);
        ID3D11DeviceContext_End(context, (ID3D11Asynchronous *)fence);
        ID3D11DeviceContext_Flush(context);
        BOOL completed = FALSE;
        HRESULT result;
        while ((result = ID3D11DeviceContext_GetData(context, (ID3D11Asynchronous *)fence,
                                                    &completed, sizeof completed, 0)) == S_FALSE && !closed) {
            if (now_us() - update_end > 10000000.0) { ok = FALSE; break; }
            pump(); SwitchToThread();
        }
        if (!ok || closed) { ok = FALSE; break; }
        check(result, "upload/copy GPU completion");
        if (!completed) { ok = FALSE; break; }
        double gpu_end = now_us();
        BOOL audited = verify_all || (notice.seq - 1) % 60 == 0;
        BOOL gpu_valid = FALSE;
        int gpu_marker = -1;
        if (audited) {
            phase = "staging readback and validation";
            ID3D11DeviceContext_CopyResource(context, (ID3D11Resource *)staging, (ID3D11Resource *)backbuffer);
            D3D11_MAPPED_SUBRESOURCE mapped;
            check(ID3D11DeviceContext_Map(context, (ID3D11Resource *)staging, 0, D3D11_MAP_READ, 0, &mapped), "GPU verification readback");
            gpu_marker = counter(mapped.pData, mapped.RowPitch);
            gpu_valid = mapped.RowPitch >= STRIDE && gpu_marker == marker && bars(mapped.pData, mapped.RowPitch) &&
                (!require_overlay || overlay(mapped.pData, mapped.RowPitch));
            for (int y = 0; y < HEIGHT && gpu_valid; ++y)
                gpu_valid = !memcmp(image + y * STRIDE, (unsigned char *)mapped.pData + y * mapped.RowPitch, STRIDE);
            ID3D11DeviceContext_Unmap(context, (ID3D11Resource *)staging, 0);
        }
        double audit_end = now_us();
        phase = "Present";
        result = IDXGISwapChain_Present(chain, 0, 0);
        double present_end = now_us();
        check(result, "Present");
        BOOL presented = result == S_OK; /* DXGI_STATUS_OCCLUDED is not successful delivery. */
        snprintf(line, sizeof line, "{\"kind\":\"consumer_frame\",\"seq\":%llu,\"marker\":%d,\"sample_hash\":%llu,"
            "\"source_valid\":%s,\"gpu_audited\":%s,\"gpu_valid\":%s,\"gpu_marker\":%d,\"presented\":%s,"
            "\"source_check_us\":%.3f,\"update_api_us\":%.3f,\"gpu_copy_completion_us\":%.3f,"
            "\"gpu_readback_validation_us\":%.3f,\"present_api_us\":%.3f,\"consumer_total_us\":%.3f}\n",
            (unsigned long long)notice.seq, marker, (unsigned long long)hash, source_valid ? "true" : "false",
            audited ? "true" : "false", gpu_valid ? "true" : "false", gpu_marker, presented ? "true" : "false",
            source_end - begin, update_end - source_end, gpu_end - update_end, audit_end - gpu_end,
            present_end - audit_end, present_end - begin);
        if (!transfer(socket, line, (int)strlen(line), TRUE)) { ok = FALSE; break; }
        if (!source_valid || (audited && !gpu_valid) || !presented) { ok = FALSE; break; }
    }
    closesocket(socket); WSACleanup();
    ID3D11DeviceContext_ClearState(context);
    ID3D11Query_Release(fence);
    ID3D11Texture2D_Release(staging); ID3D11Texture2D_Release(texture); ID3D11Texture2D_Release(backbuffer);
    IDXGIAdapter_Release(adapter); IDXGIDevice_Release(dxgi);
    IDXGISwapChain_Release(chain); ID3D11DeviceContext_Release(context); ID3D11Device_Release(device);
    DestroyWindow(hwnd);
    UnmapViewOfFile(pixels); CloseHandle(mapping); CloseHandle(file);
    return ok ? 0 : 1;
}
