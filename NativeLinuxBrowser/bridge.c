#include "bridge.h"
#include <arpa/inet.h>
#include <errno.h>
#include <fcntl.h>
#include <netinet/tcp.h>
#include <poll.h>
#include <stdio.h>
#include <stdatomic.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <sys/random.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <unistd.h>

/* One authenticated client per helper. Pixels are copied before a slot is
 * advertised; shared slots cannot be reused until the reader acknowledges them.
 * There is at most one pending frame and a bounded queue of control messages. */
enum { FREE, PENDING, SENDING, LEASED };
typedef struct {
    int state;
    unsigned width, height;
    uint64_t sequence;
    int64_t timestamp;
} Slot;
struct Bridge {
    int listener, peer, mapping_fd;
    unsigned char *mapping;
    size_t slot_size;
    char *directory, *endpoint, *mapping_path;
    char token[65];
    GMutex lock;
    GCond wake;
    GQueue messages;
    GThread *reader, *writer;
    _Atomic gboolean quitting;
    _Atomic unsigned pending_commands;
    gboolean connected, shared, owns_directory;
    Slot slots[CC_SLOT_COUNT];
    uint64_t sequence, dropped;
    BridgeCommand command;
    BridgeDisconnected disconnected;
    void *user;
};

static gboolean exact(int fd, void *data, size_t length, gboolean writing)
{
    size_t position = 0;
    while (position < length) {
        ssize_t count = writing ? send(fd, (char *)data + position, length - position, MSG_NOSIGNAL)
                                : recv(fd, (char *)data + position, length - position, 0);
        if (count < 0 && errno == EINTR) continue;
        if (count <= 0) return FALSE;
        position += (size_t)count;
    }
    return TRUE;
}

static gboolean packet(int fd, uint32_t kind, const void *data, uint32_t length)
{
    uint32_t header[] = { GUINT32_TO_LE(0x31424343), GUINT32_TO_LE(kind), GUINT32_TO_LE(length), 0 };
    return exact(fd, header, sizeof header, TRUE) && exact(fd, (void *)data, length, TRUE);
}

static JsonParser *read_json(int fd)
{
    uint32_t header[4];
    if (!exact(fd, header, sizeof header, FALSE) || GUINT32_FROM_LE(header[0]) != 0x31424343
        || GUINT32_FROM_LE(header[1]) != 1 || header[3] != 0) return NULL;
    uint32_t size = GUINT32_FROM_LE(header[2]);
    if (size == 0 || size > CC_MAX_JSON) return NULL;
    char *json = g_malloc(size + 1);
    if (!exact(fd, json, size, FALSE)) { g_free(json); return NULL; }
    json[size] = 0;
    JsonParser *parser = json_parser_new();
    if (!json_parser_load_from_data(parser, json, size, NULL)
        || !JSON_NODE_HOLDS_OBJECT(json_parser_get_root(parser))) g_clear_object(&parser);
    g_free(json);
    return parser;
}

static const char *string_member(JsonObject *object, const char *name)
{
    JsonNode *node = json_object_get_member(object, name);
    return node && JSON_NODE_HOLDS_VALUE(node) && json_node_get_value_type(node) == G_TYPE_STRING
        ? json_node_get_string(node) : "";
}

typedef struct { Bridge *bridge; JsonParser *parser; } Dispatch;
static gboolean dispatch_command(gpointer data)
{
    Dispatch *dispatch = data;
    if (!dispatch->bridge->quitting)
        dispatch->bridge->command(json_node_get_object(json_parser_get_root(dispatch->parser)), dispatch->bridge->user);
    atomic_fetch_sub(&dispatch->bridge->pending_commands, 1);
    g_object_unref(dispatch->parser);
    g_free(dispatch);
    return G_SOURCE_REMOVE;
}
static gboolean disconnected(gpointer data)
{
    Bridge *bridge = data;
    if (!bridge->quitting) bridge->disconnected(bridge->user);
    return G_SOURCE_REMOVE;
}

static void fail_connection(Bridge *bridge)
{
    g_mutex_lock(&bridge->lock);
    if (bridge->connected) {
        bridge->connected = FALSE;
        shutdown(bridge->peer, SHUT_RDWR);
        g_cond_broadcast(&bridge->wake);
        g_idle_add_full(G_PRIORITY_DEFAULT, disconnected, bridge, NULL);
    }
    g_mutex_unlock(&bridge->lock);
}

static gpointer read_loop(gpointer data)
{
    Bridge *bridge = data;
    const gint64 deadline = g_get_monotonic_time() + 20 * G_TIME_SPAN_SECOND;
    while (!bridge->quitting && g_get_monotonic_time() < deadline) {
        struct pollfd wait = { .fd = bridge->listener, .events = POLLIN };
        if (poll(&wait, 1, 100) <= 0) continue;
        int peer = accept4(bridge->listener, NULL, NULL, SOCK_CLOEXEC);
        if (peer < 0) continue;
        int no_delay = 1;
        setsockopt(peer, IPPROTO_TCP, TCP_NODELAY, &no_delay, sizeof no_delay);
        struct timeval timeout = { .tv_sec = 3 };
        setsockopt(peer, SOL_SOCKET, SO_RCVTIMEO, &timeout, sizeof timeout);
        setsockopt(peer, SOL_SOCKET, SO_SNDTIMEO, &timeout, sizeof timeout);
        JsonParser *hello = read_json(peer);
        JsonObject *object = hello ? json_node_get_object(json_parser_get_root(hello)) : NULL;
        const char *token = object ? string_member(object, "token") : "";
        size_t token_length = strlen(token);
        unsigned difference = token_length == 64 ? 0 : 1;
        for (unsigned i = 0; i < 64; ++i) difference |= bridge->token[i] ^ (i < token_length ? token[i] : 0);
        gboolean valid = object && !strcmp(string_member(object, "type"), "hello") && difference == 0
            && json_object_get_int_member_with_default(object, "version", 0) == 1;
        if (!valid) { g_clear_object(&hello); close(peer); continue; }
        g_mutex_lock(&bridge->lock);
        bridge->peer = peer;
        bridge->shared = !strcmp(string_member(object, "transport"), "shared-file");
        char *response = g_strdup_printf("{\"type\":\"hello\",\"version\":1,\"transport\":\"%s\",\"slotSize\":%zu,\"slots\":%d}",
                                         bridge->shared ? "shared-file" : "tcp", bridge->slot_size, CC_SLOT_COUNT);
        g_queue_push_head(&bridge->messages, response);
        bridge->connected = TRUE;
        g_cond_broadcast(&bridge->wake);
        g_mutex_unlock(&bridge->lock);
        g_clear_object(&hello);
        timeout.tv_sec = 12; /* Heartbeats, not media frames, keep a paused session alive. */
        setsockopt(peer, SOL_SOCKET, SO_RCVTIMEO, &timeout, sizeof timeout);
        while (!bridge->quitting) {
            JsonParser *parser = read_json(peer);
            if (!parser) break;
            /* Bound main-loop work as well as outgoing data. A stalled UI must
             * not let even an authenticated peer allocate an unlimited queue. */
            if (atomic_fetch_add(&bridge->pending_commands, 1) >= 64) {
                atomic_fetch_sub(&bridge->pending_commands, 1);
                g_object_unref(parser);
                break;
            }
            Dispatch *dispatch = g_new(Dispatch, 1);
            *dispatch = (Dispatch) { bridge, parser };
            g_idle_add_full(G_PRIORITY_DEFAULT, dispatch_command, dispatch, NULL);
        }
        fail_connection(bridge);
        return NULL;
    }
    g_idle_add_full(G_PRIORITY_DEFAULT, disconnected, bridge, NULL);
    return NULL;
}

static gpointer write_loop(gpointer data)
{
    Bridge *bridge = data;
    while (TRUE) {
        g_mutex_lock(&bridge->lock);
        int index = -1;
        for (unsigned i = 0; i < CC_SLOT_COUNT; ++i)
            if (bridge->slots[i].state == PENDING) index = (int)i;
        while (!bridge->quitting && (!bridge->connected || (index < 0 && g_queue_is_empty(&bridge->messages)))) {
            g_cond_wait(&bridge->wake, &bridge->lock);
            for (unsigned i = 0; i < CC_SLOT_COUNT; ++i)
                if (bridge->slots[i].state == PENDING) index = (int)i;
        }
        if (bridge->quitting) { g_mutex_unlock(&bridge->lock); return NULL; }
        char *message = g_queue_pop_head(&bridge->messages);
        Slot slot = {0};
        if (!message && index >= 0) {
            slot = bridge->slots[index];
            bridge->slots[index].state = bridge->shared ? LEASED : SENDING;
        }
        int peer = bridge->peer;
        gboolean shared = bridge->shared;
        g_mutex_unlock(&bridge->lock);
        gboolean success;
        if (message) {
            success = packet(peer, 1, message, (uint32_t)strlen(message));
            g_free(message);
        } else if (shared) {
            char json[320];
            int size = snprintf(json, sizeof json, "{\"type\":\"frame\",\"slot\":%d,\"width\":%u,\"height\":%u,\"stride\":%u,\"sequence\":%" G_GUINT64_FORMAT ",\"timestamp\":%" G_GINT64_FORMAT "}",
                                index, slot.width, slot.height, slot.width * 4, slot.sequence, slot.timestamp);
            success = packet(peer, 1, json, (uint32_t)size);
        } else {
            uint32_t length = slot.width * slot.height * 4 + 32;
            uint32_t header[] = { GUINT32_TO_LE(0x31424343), GUINT32_TO_LE(2), GUINT32_TO_LE(length), 0 };
            uint32_t dimensions[] = { GUINT32_TO_LE(slot.width), GUINT32_TO_LE(slot.height), GUINT32_TO_LE(slot.width * 4), GUINT32_TO_LE(1) };
            uint64_t timing[] = { GUINT64_TO_LE(slot.sequence), GUINT64_TO_LE((uint64_t)slot.timestamp) };
            success = exact(peer, header, sizeof header, TRUE) && exact(peer, dimensions, sizeof dimensions, TRUE)
                && exact(peer, timing, sizeof timing, TRUE)
                && exact(peer, bridge->mapping + index * bridge->slot_size, length - 32, TRUE);
            g_mutex_lock(&bridge->lock);
            bridge->slots[index].state = FREE;
            g_mutex_unlock(&bridge->lock);
        }
        if (!success) { fail_connection(bridge); return NULL; }
    }
}

static gboolean private_directory(const char *path, GError **error)
{
    if (mkdir(path, 0700) < 0 && errno != EEXIST) goto fail;
    struct stat status;
    if (lstat(path, &status) < 0 || !S_ISDIR(status.st_mode) || status.st_uid != getuid() || (status.st_mode & 077)) {
        errno = EACCES;
        goto fail;
    }
    return TRUE;
fail:
    g_set_error(error, G_FILE_ERROR, g_file_error_from_errno(errno), "Private runtime directory unavailable: %s", g_strerror(errno));
    return FALSE;
}

Bridge *bridge_new(const char *session, const char *root, BridgeCommand command,
                   BridgeDisconnected on_disconnect, void *user, GError **error)
{
    if (!session || strlen(session) != 32 || strspn(session, "0123456789abcdef") != 32 || !g_path_is_absolute(root)) {
        g_set_error_literal(error, G_FILE_ERROR, G_FILE_ERROR_INVAL, "Invalid runtime root or session identifier");
        return NULL;
    }
    Bridge *bridge = g_new0(Bridge, 1);
    bridge->listener = bridge->peer = bridge->mapping_fd = -1;
    bridge->command = command;
    bridge->disconnected = on_disconnect;
    bridge->user = user;
    bridge->slot_size = CC_MAX_WIDTH * CC_MAX_HEIGHT * 4;
    g_mutex_init(&bridge->lock);
    g_cond_init(&bridge->wake);
    g_queue_init(&bridge->messages);
    char *parent = g_build_filename(root, "crystalcast", NULL);
    bridge->directory = g_build_filename(parent, session, NULL);
    gboolean private = private_directory(root, error) && private_directory(parent, error);
    g_free(parent);
    if (!private) goto fail;
    /* Never attach to a pre-existing session, including a symlink. */
    if (mkdir(bridge->directory, 0700) < 0) goto io_fail;
    bridge->owns_directory = TRUE;
    bridge->mapping_path = g_build_filename(bridge->directory, "frames.bin", NULL);
    bridge->mapping_fd = open(bridge->mapping_path, O_CREAT | O_EXCL | O_RDWR | O_CLOEXEC | O_NOFOLLOW, 0600);
    if (bridge->mapping_fd < 0 || ftruncate(bridge->mapping_fd, bridge->slot_size * CC_SLOT_COUNT) < 0) goto io_fail;
    bridge->mapping = mmap(NULL, bridge->slot_size * CC_SLOT_COUNT, PROT_READ | PROT_WRITE, MAP_SHARED, bridge->mapping_fd, 0);
    if (bridge->mapping == MAP_FAILED) { bridge->mapping = NULL; goto io_fail; }
    unsigned char random[32];
    if (getrandom(random, sizeof random, 0) != sizeof random) goto io_fail;
    for (unsigned i = 0; i < sizeof random; ++i) snprintf(bridge->token + i * 2, 3, "%02x", random[i]);
    bridge->listener = socket(AF_INET, SOCK_STREAM | SOCK_CLOEXEC, 0);
    struct sockaddr_in address = { .sin_family = AF_INET, .sin_addr.s_addr = htonl(INADDR_LOOPBACK), .sin_port = 0 };
    socklen_t address_length = sizeof address;
    if (bridge->listener < 0 || bind(bridge->listener, (void *)&address, sizeof address) < 0
        || listen(bridge->listener, 4) < 0 || getsockname(bridge->listener, (void *)&address, &address_length) < 0) goto io_fail;
    bridge->endpoint = g_build_filename(bridge->directory, "endpoint.json", NULL);
    int endpoint = open(bridge->endpoint, O_WRONLY | O_CREAT | O_EXCL | O_CLOEXEC | O_NOFOLLOW, 0600);
    if (endpoint < 0) goto io_fail;
    char json[256];
    int size = snprintf(json, sizeof json, "{\"version\":1,\"port\":%u,\"token\":\"%s\",\"pid\":%d}\n", ntohs(address.sin_port), bridge->token, getpid());
    gboolean written = write(endpoint, json, (size_t)size) == size;
    close(endpoint);
    if (!written) goto io_fail;
    bridge->writer = g_thread_new("cc-writer", write_loop, bridge);
    bridge->reader = g_thread_new("cc-reader", read_loop, bridge);
    return bridge;
io_fail:
    g_set_error(error, G_FILE_ERROR, g_file_error_from_errno(errno), "Browser bridge setup failed: %s", g_strerror(errno));
fail:
    bridge_free(bridge);
    return NULL;
}

void bridge_json(Bridge *bridge, const char *json)
{
    if (!json || strlen(json) > CC_MAX_JSON) return;
    g_mutex_lock(&bridge->lock);
    if (bridge->connected && !bridge->quitting && g_queue_get_length(&bridge->messages) < 64) {
        g_queue_push_tail(&bridge->messages, g_strdup(json));
        g_cond_signal(&bridge->wake);
    }
    g_mutex_unlock(&bridge->lock);
}

void bridge_frame(Bridge *bridge, const unsigned char *pixels, unsigned width, unsigned height, unsigned stride, int64_t timestamp)
{
    if (!width || !height || width > CC_MAX_WIDTH || height > CC_MAX_HEIGHT || stride < width * 4) return;
    g_mutex_lock(&bridge->lock);
    if (!bridge->connected) { g_mutex_unlock(&bridge->lock); return; }
    int free_slot = -1;
    for (unsigned i = 0; i < CC_SLOT_COUNT; ++i) {
        if (bridge->slots[i].state == PENDING) { bridge->slots[i].state = FREE; bridge->dropped++; }
        if (bridge->slots[i].state == FREE) free_slot = (int)i;
    }
    if (free_slot < 0) { bridge->dropped++; g_mutex_unlock(&bridge->lock); return; }
    unsigned char *target = bridge->mapping + free_slot * bridge->slot_size;
    for (unsigned y = 0; y < height; ++y) memcpy(target + y * width * 4, pixels + y * stride, width * 4);
    bridge->slots[free_slot] = (Slot) { PENDING, width, height, ++bridge->sequence, timestamp };
    g_cond_signal(&bridge->wake);
    g_mutex_unlock(&bridge->lock);
}
void bridge_ack(Bridge *bridge, unsigned slot, uint64_t sequence)
{
    g_mutex_lock(&bridge->lock);
    if (slot < CC_SLOT_COUNT && bridge->slots[slot].state == LEASED && bridge->slots[slot].sequence == sequence)
        bridge->slots[slot].state = FREE;
    g_mutex_unlock(&bridge->lock);
}
gboolean bridge_connected(Bridge *bridge)
{
    g_mutex_lock(&bridge->lock);
    gboolean connected = bridge->connected;
    g_mutex_unlock(&bridge->lock);
    return connected;
}
uint64_t bridge_frame_count(Bridge *bridge) { return bridge->sequence; }
uint64_t bridge_drop_count(Bridge *bridge) { return bridge->dropped; }

void bridge_free(Bridge *bridge)
{
    if (!bridge) return;
    g_mutex_lock(&bridge->lock);
    bridge->quitting = TRUE;
    if (bridge->peer >= 0) shutdown(bridge->peer, SHUT_RDWR);
    g_cond_broadcast(&bridge->wake);
    g_mutex_unlock(&bridge->lock);
    if (bridge->reader) g_thread_join(bridge->reader);
    if (bridge->writer) g_thread_join(bridge->writer);
    /* Drain callbacks while the bridge and its user are still alive. */
    while (g_main_context_pending(NULL)) g_main_context_iteration(NULL, FALSE);
    if (bridge->peer >= 0) close(bridge->peer);
    if (bridge->listener >= 0) close(bridge->listener);
    if (bridge->mapping) munmap(bridge->mapping, bridge->slot_size * CC_SLOT_COUNT);
    if (bridge->mapping_fd >= 0) close(bridge->mapping_fd);
    g_queue_clear_full(&bridge->messages, g_free);
    if (bridge->endpoint) unlink(bridge->endpoint);
    if (bridge->mapping_path) unlink(bridge->mapping_path);
    if (bridge->owns_directory) rmdir(bridge->directory);
    g_free(bridge->endpoint); g_free(bridge->mapping_path); g_free(bridge->directory);
    g_mutex_clear(&bridge->lock); g_cond_clear(&bridge->wake);
    g_free(bridge);
}
