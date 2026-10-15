#pragma once
#include <glib.h>
#include <json-glib/json-glib.h>
#include <stdint.h>

#define CC_MAX_WIDTH 1920
#define CC_MAX_HEIGHT 1080
#define CC_SLOT_COUNT 3
#define CC_MAX_JSON (512 * 1024)

typedef struct Bridge Bridge;
typedef void (*BridgeCommand)(JsonObject *, void *);
typedef void (*BridgeDisconnected)(void *);

Bridge *bridge_new(const char *session, const char *runtime_root, BridgeCommand command,
                   BridgeDisconnected disconnected, void *user, GError **error);
void bridge_json(Bridge *, const char *json);
void bridge_frame(Bridge *, const unsigned char *pixels, unsigned width, unsigned height,
                  unsigned stride, int64_t timestamp);
void bridge_ack(Bridge *, unsigned slot, uint64_t sequence);
void bridge_free(Bridge *);
gboolean bridge_connected(Bridge *);
uint64_t bridge_frame_count(Bridge *);
uint64_t bridge_drop_count(Bridge *);
