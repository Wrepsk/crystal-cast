/* Developer experiment: public GTK4 widget rendering, no WebKit snapshot API.
 * Local trusted test pages only. This is not the plugin's browser backend. */
#define _GNU_SOURCE
#include <gtk/gtk.h>
#include <webkit/webkit.h>
#include <gst/gst.h>
#include <epoxy/gl.h>
#include <epoxy/egl.h>
#include <errno.h>
#include <fcntl.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <sys/mman.h>
#include <sys/socket.h>
#include <unistd.h>

enum { WIDTH = 1920, HEIGHT = 1080, STRIDE = WIDTH * 4, SLOTS = 3 };
#define FRAME_BYTES ((size_t) STRIDE * HEIGHT)
static GtkWidget *window, *view, *preview_window, *picture;
static GdkPaintable *paintable;
static GMainLoop *loop;
static char *uri, *mode, *shm_path, *node_path, *png_path;
static int seconds = 15, warmup = 5, audit_every = 60, ack_fd = -1, hide_after;
static int poll_hz;
static guint pending;
static gint64 started;
static uint64_t sequence, leases[SLOTS], invalidations, drops, empty;
static unsigned char *mapping, *audit;
static gboolean shm_mode, direct_mode, dumped, saved, failed;
static gboolean software_webkit;
static gboolean preview;
static gboolean software_web_process;
static gboolean check_only, require_direct, unavailable;
static int window_width = WIDTH, window_height = HEIGHT;
static gboolean wait_for_video, video_ready;
static guint stop_source;
static gboolean stop(gpointer unused);
static gboolean hide_window(gpointer unused);

static void json_string(const char *value)
{
    putchar('"');
    for (const unsigned char *p = (const unsigned char *) (value ? value : "unknown"); *p; ++p) {
        if (*p == '"' || *p == '\\') printf("\\%c", *p);
        else if (*p < 32) printf("\\u%04x", *p);
        else putchar(*p);
    }
    putchar('"');
}

/* Inventory the actual browser node tree, independently of the output texture.
 * Unknown future nodes are explicit rather than evidence of no DMA-BUF. */
typedef struct { unsigned dmabuf, gl, memory, cairo, unknown; } Inventory;
static void inventory(GskRenderNode *node, Inventory *out, int depth)
{
    if (depth > 64) { ++out->unknown; return; }
    switch (gsk_render_node_get_node_type(node)) {
    case GSK_TEXTURE_NODE:
    case GSK_TEXTURE_SCALE_NODE: {
        GdkTexture *t = gsk_render_node_get_node_type(node) == GSK_TEXTURE_NODE
            ? gsk_texture_node_get_texture(node) : gsk_texture_scale_node_get_texture(node);
        if (GDK_IS_DMABUF_TEXTURE(t)) ++out->dmabuf;
        else if (GDK_IS_GL_TEXTURE(t)) ++out->gl;
        else if (GDK_IS_MEMORY_TEXTURE(t)) ++out->memory;
        else ++out->unknown;
        break;
    }
    case GSK_CAIRO_NODE: ++out->cairo; break;
    case GSK_CONTAINER_NODE:
        for (guint i = 0; i < gsk_container_node_get_n_children(node); ++i)
            inventory(gsk_container_node_get_child(node, i), out, depth + 1);
        break;
#define CHILD(kind, name) case kind: inventory(name(node), out, depth + 1); break
    CHILD(GSK_TRANSFORM_NODE, gsk_transform_node_get_child);
    CHILD(GSK_OPACITY_NODE, gsk_opacity_node_get_child);
    CHILD(GSK_COLOR_MATRIX_NODE, gsk_color_matrix_node_get_child);
    CHILD(GSK_REPEAT_NODE, gsk_repeat_node_get_child);
    CHILD(GSK_CLIP_NODE, gsk_clip_node_get_child);
    CHILD(GSK_ROUNDED_CLIP_NODE, gsk_rounded_clip_node_get_child);
    CHILD(GSK_SHADOW_NODE, gsk_shadow_node_get_child);
    CHILD(GSK_BLUR_NODE, gsk_blur_node_get_child);
    CHILD(GSK_DEBUG_NODE, gsk_debug_node_get_child);
    CHILD(GSK_SUBSURFACE_NODE, gsk_subsurface_node_get_child);
#undef CHILD
    case GSK_BLEND_NODE:
        inventory(gsk_blend_node_get_bottom_child(node), out, depth + 1);
        inventory(gsk_blend_node_get_top_child(node), out, depth + 1); break;
    case GSK_CROSS_FADE_NODE:
        inventory(gsk_cross_fade_node_get_start_child(node), out, depth + 1);
        inventory(gsk_cross_fade_node_get_end_child(node), out, depth + 1); break;
    case GSK_COLOR_NODE: case GSK_LINEAR_GRADIENT_NODE: case GSK_RADIAL_GRADIENT_NODE:
    case GSK_REPEATING_LINEAR_GRADIENT_NODE: case GSK_REPEATING_RADIAL_GRADIENT_NODE:
    case GSK_CONIC_GRADIENT_NODE: case GSK_BORDER_NODE: case GSK_TEXT_NODE:
    case GSK_INSET_SHADOW_NODE: case GSK_OUTSET_SHADOW_NODE: break;
    default: ++out->unknown; break;
    }
}

static void check_runtime(void)
{
    gst_init(NULL, NULL);
    guint major, minor, micro, nano;
    gst_version(&major, &minor, &micro, &nano);
    printf("{\"kind\":\"runtime\",\"gtk\":\"%d.%d.%d\",\"webkit\":\"%d.%d.%d\",\"gstreamer\":\"%u.%u.%u\",\"factories\":{",
        gtk_get_major_version(), gtk_get_minor_version(), gtk_get_micro_version(),
        webkit_get_major_version(), webkit_get_minor_version(), webkit_get_micro_version(), major, minor, micro);
    const char *names[] = { "qtdemux", "h264parse", "videoconvert", "avdec_h264", "openh264dec",
        "vah264dec", "vaapih264dec", "nvh264dec", "v4l2slh264dec", "v4l2h264dec", NULL };
    for (int i = 0; names[i]; ++i) {
        GstElementFactory *factory = gst_element_factory_find(names[i]);
        if (i) putchar(',');
        json_string(names[i]); printf(":%s", factory ? "true" : "false");
        if (factory) gst_object_unref(factory);
    }
    printf("}}\n");
}

/* Accept only a texture covering the exact viewport, with no transform,
 * overlay, opacity, scaling or clipping that could change its pixels. */
static GdkTexture *exact_texture(GskRenderNode *node)
{
    graphene_rect_t viewport = GRAPHENE_RECT_INIT(0, 0, WIDTH, HEIGHT), bounds;
    if (gsk_render_node_get_node_type(node) == GSK_CLIP_NODE) {
        if (!graphene_rect_contains_rect(gsk_clip_node_get_clip(node), &viewport))
            return NULL;
        return exact_texture(gsk_clip_node_get_child(node));
    }
    if (gsk_render_node_get_node_type(node) != GSK_TEXTURE_NODE)
        return NULL;
    gsk_render_node_get_bounds(node, &bounds);
    GdkTexture *texture = gsk_texture_node_get_texture(node);
    if (!graphene_rect_equal(&bounds, &viewport) ||
        gdk_texture_get_width(texture) != WIDTH ||
        gdk_texture_get_height(texture) != HEIGHT)
        return NULL;
    return texture;
}

static void inspect_node(GskRenderNode *node, int depth)
{
    GskRenderNodeType type = gsk_render_node_get_node_type(node);
    graphene_rect_t b;
    gsk_render_node_get_bounds(node, &b);
    fprintf(stderr, "%*snode=%d bounds=%.0f,%.0f %.0fx%.0f", depth * 2, "",
            type, b.origin.x, b.origin.y, b.size.width, b.size.height);
    if (type == GSK_TEXTURE_NODE)
        fprintf(stderr, " texture=%s", G_OBJECT_TYPE_NAME(gsk_texture_node_get_texture(node)));
    fputc('\n', stderr);
    if (depth >= 20) return;
    switch (type) {
    case GSK_CONTAINER_NODE:
        for (guint i = 0; i < gsk_container_node_get_n_children(node); ++i)
            inspect_node(gsk_container_node_get_child(node, i), depth + 1);
        break;
    case GSK_CLIP_NODE: inspect_node(gsk_clip_node_get_child(node), depth + 1); break;
    case GSK_ROUNDED_CLIP_NODE: inspect_node(gsk_rounded_clip_node_get_child(node), depth + 1); break;
    case GSK_TRANSFORM_NODE: inspect_node(gsk_transform_node_get_child(node), depth + 1); break;
    case GSK_OPACITY_NODE: inspect_node(gsk_opacity_node_get_child(node), depth + 1); break;
    default: break;
    }
}

static void quit_error(const char *message)
{
    fprintf(stderr, "probe failed: %s\n", message);
    failed = TRUE;
    g_main_loop_quit(loop);
}

static int available_slot(void)
{
    uint64_t ack;
    ssize_t size;
    while ((size = recv(ack_fd, &ack, sizeof ack, MSG_DONTWAIT)) > 0) {
        if (size != sizeof ack) { quit_error("malformed acknowledgement"); return -1; }
        gboolean found = FALSE;
        for (int i = 0; i < SLOTS; ++i) {
            if (leases[i] == ack && ack != 0) {
                leases[i] = 0;
                found = TRUE;
                break;
            }
        }
        if (!found) { quit_error("unknown or duplicate acknowledgement"); return -1; }
    }
    if (size == 0 || (size < 0 && errno != EAGAIN && errno != EWOULDBLOCK)) {
        quit_error("consumer disconnected"); return -1;
    }
    for (int i = 0; i < SLOTS; ++i) if (!leases[i]) return i;
    return -1;
}

static gboolean capture(gpointer unused)
{
    (void) unused;
    pending = 0;
    if (wait_for_video && !video_ready) return G_SOURCE_REMOVE;
    gint64 begin = g_get_monotonic_time();
    if (begin - started >= (gint64) (warmup + seconds) * G_USEC_PER_SEC) {
        g_main_loop_quit(loop);
        return G_SOURCE_REMOVE;
    }
    if (begin - started < (gint64) warmup * G_USEC_PER_SEC) return G_SOURCE_REMOVE;
    int slot = shm_mode ? available_slot() : -1;
    if (shm_mode && slot < 0) { ++drops; return G_SOURCE_REMOVE; }
    GtkSnapshot *snapshot = gtk_snapshot_new();
    gdk_paintable_snapshot(paintable, GDK_SNAPSHOT(snapshot), WIDTH, HEIGHT);
    GskRenderNode *node = gtk_snapshot_free_to_node(snapshot);
    gint64 snap_end = g_get_monotonic_time();
    if (!node) { ++empty; return G_SOURCE_REMOVE; }
    GdkTexture *source = exact_texture(node);
    Inventory observed = { 0 };
    inventory(node, &observed, 0);
    if (!dumped)
        printf("{\"kind\":\"geometry\",\"browser_width\":%d,\"browser_height\":%d,\"window_width\":%d,\"window_height\":%d}\n",
               gtk_widget_get_width(view), gtk_widget_get_height(view),
               gtk_widget_get_width(window), gtk_widget_get_height(window));
    if (require_direct && !source) {
        printf("{\"kind\":\"unavailable\",\"reason\":\"Browser node is not an exact full-viewport texture; direct mode would require rendering or scaling\","
               "\"browser_dmabuf\":%u,\"browser_gl\":%u,\"browser_memory\":%u,\"browser_cairo\":%u,\"uninspected_nodes\":%u}\n",
               observed.dmabuf, observed.gl, observed.memory, observed.cairo, observed.unknown);
        unavailable = TRUE;
        gsk_render_node_unref(node);
        g_main_loop_quit(loop);
        return G_SOURCE_REMOVE;
    }
    GdkTexture *texture;
    gboolean direct = direct_mode && source;
    if (direct)
        texture = g_object_ref(source);
    else {
        graphene_rect_t viewport = GRAPHENE_RECT_INIT(0, 0, WIDTH, HEIGHT);
        texture = gsk_renderer_render_texture(gtk_native_get_renderer(GTK_NATIVE(window)), node, &viewport);
    }
    gint64 render_end = g_get_monotonic_time();
    if (!texture) { gsk_render_node_unref(node); quit_error("no rendered texture"); return G_SOURCE_REMOVE; }
    if (!dumped && gdk_gl_context_get_current())
        fprintf(stderr, "GSK current GL renderer=%s\n", glGetString(GL_RENDERER));
    if (!dumped) {
        printf("{\"kind\":\"render_context\",\"gl_renderer\":");
        json_string(gdk_gl_context_get_current() ? (const char *) glGetString(GL_RENDERER) : "not observable");
        printf("}\n");
    }
    ++sequence;
    gboolean download = shm_mode || (audit_every > 0 && (sequence - 1) % audit_every == 0);
    unsigned char *pixels = shm_mode ? mapping + FRAME_BYTES * slot : audit;
    gint64 download_begin = g_get_monotonic_time();
    if (download) gdk_texture_download(texture, pixels, STRIDE);
    gint64 download_end = g_get_monotonic_time();
    const char *texture_type = G_OBJECT_TYPE_NAME(texture);
    const char *source_type = source ? G_OBJECT_TYPE_NAME(source) : "composite";
    char *output_type = g_strdup(texture_type), *input_type = g_strdup(source_type);
    /* A native, same-process texture consumer. GtkPicture holds a reference;
     * no per-frame download is needed for this handoff. No Wine interop implied. */
    if (picture) gtk_picture_set_paintable(GTK_PICTURE(picture), GDK_PAINTABLE(texture));
    g_object_unref(texture);
    gint64 release_end = g_get_monotonic_time();
    /* Fixed-width little-endian Linux x86_64 test protocol: seq, slot, bytes, time. */
    if (shm_mode) {
        struct { uint64_t seq; uint32_t slot, bytes; int64_t time; } notice =
            { sequence, (uint32_t) slot, FRAME_BYTES, begin };
        leases[slot] = sequence;
        if (send(ack_fd, &notice, sizeof notice, MSG_DONTWAIT | MSG_NOSIGNAL) != sizeof notice)
            quit_error("consumer notification failed");
    }
    int marker = -1;
    uint64_t sample_hash = 14695981039346656037ULL;
    gboolean bars_valid = FALSE, overlay_valid = FALSE;
    if (download) {
        marker = 0;
        for (int bit = 0; bit < 10; ++bit) {
            size_t offset = 24 * STRIDE + (bit * 96 + 40) * 4;
            if (pixels[offset] > 127) marker |= 1 << bit;
        }
        for (size_t i = 0; i < FRAME_BYTES; i += 4096)
            sample_hash = (sample_hash ^ pixels[i]) * 1099511628211ULL;
        const unsigned char *r = pixels + (1040 * WIDTH + 80) * 4;
        const unsigned char *g = pixels + (1040 * WIDTH + 240) * 4;
        const unsigned char *b = pixels + (1040 * WIDTH + 400) * 4;
        bars_valid = r[2] > 180 && r[0] < 40 && g[1] > 180 && g[0] < 40 &&
                     b[0] > 180 && b[2] < 40 && r[3] == 255 && g[3] == 255 && b[3] == 255;
        const unsigned char *o = pixels + (810 * WIDTH + 1510) * 4;
        overlay_valid = o[0] == 255 && o[1] == 255 && o[2] == 0 && o[3] == 255;
    }
    gint64 end = g_get_monotonic_time();
    printf("{\"kind\":\"frame\",\"seq\":%lu,\"time_us\":%ld,\"snapshot_us\":%ld,"
           "\"render_us\":%ld,\"download_us\":%ld,\"release_us\":%ld,\"total_us\":%ld,"
           "\"downloaded\":%s,\"marker\":%d,\"direct\":%s,\"source\":\"%s\",\"texture\":\"%s\","
           "\"browser_dmabuf\":%u,\"browser_gl\":%u,\"browser_memory\":%u,\"browser_cairo\":%u,\"uninspected_nodes\":%u,"
           "\"sample_hash\":%lu,\"bars_valid\":%s,\"overlay_valid\":%s,\"widget_width\":%d,\"widget_height\":%d}\n",
           sequence, begin, snap_end - begin, render_end - snap_end,
           download_end - download_begin, release_end - download_end, end - begin,
           download ? "true" : "false", marker, direct ? "true" : "false", input_type, output_type,
           observed.dmabuf, observed.gl, observed.memory, observed.cairo, observed.unknown,
           sample_hash, bars_valid ? "true" : "false", overlay_valid ? "true" : "false",
           gtk_widget_get_width(view), gtk_widget_get_height(view));
    g_free(input_type); g_free(output_type);
    /* Diagnostics are optional and outside measured extraction time. */
    if (!dumped) {
        inspect_node(node, 0);
        if (node_path) gsk_render_node_write_to_file(node, node_path, NULL);
        dumped = TRUE;
    }
    if (png_path && !saved) {
        graphene_rect_t viewport = GRAPHENE_RECT_INIT(0, 0, WIDTH, HEIGHT);
        GdkTexture *image = gsk_renderer_render_texture(gtk_native_get_renderer(GTK_NATIVE(window)), node, &viewport);
        if (!gdk_texture_save_to_png(image, png_path)) quit_error("PNG write failed");
        g_object_unref(image);
        saved = TRUE;
    }
    gsk_render_node_unref(node);
    return G_SOURCE_REMOVE;
}

static void invalidated(GdkPaintable *p, gpointer unused)
{
    (void) p; (void) unused;
    ++invalidations;
    /* GtkWidgetPaintable publishes its new cached image in a high-priority
     * idle. A default-priority idle can starve behind browser IPC at 60 Hz. */
    if (!poll_hz && !pending) pending = g_idle_add_full(G_PRIORITY_HIGH, capture, NULL, NULL);
}

static gboolean poll_capture(gpointer unused)
{
    static gint64 next;
    gint64 now = g_get_monotonic_time();
    if (now >= next) {
        capture(unused);
        next = now + G_USEC_PER_SEC / poll_hz;
    }
    return G_SOURCE_CONTINUE;
}

static void media_message(WebKitUserContentManager *manager, JSCValue *value, gpointer unused)
{
    (void) manager; (void) unused;
    char *json = jsc_value_to_string(value);
    printf("{\"kind\":\"media\",\"time_us\":%ld,\"data\":%s}\n", g_get_monotonic_time(), json);
    g_free(json);
}

static void ready_message(WebKitUserContentManager *manager, JSCValue *value, gpointer unused)
{
    (void) manager; (void) value; (void) unused;
    if (!wait_for_video || video_ready) return;
    video_ready = TRUE;
    if (stop_source) g_source_remove(stop_source);
    started = g_get_monotonic_time();
    printf("{\"kind\":\"measurement_start\",\"time_us\":%ld}\n", started);
    stop_source = g_timeout_add((warmup + seconds) * 1000, stop, NULL);
    if (hide_after > 0) g_timeout_add((warmup + hide_after) * 1000, hide_window, NULL);
}

static gboolean startup_timeout(gpointer unused)
{
    (void) unused;
    printf("{\"kind\":\"unavailable\",\"reason\":\"Local H.264 playback was not ready within 30 seconds; codec, sandbox or browser initialization unavailable\"}\n");
    unavailable = TRUE;
    g_main_loop_quit(loop);
    return G_SOURCE_REMOVE;
}

static gboolean stop(gpointer unused)
{
    (void) unused;
    g_main_loop_quit(loop);
    return G_SOURCE_REMOVE;
}

static gboolean close_window(GtkWindow *widget, gpointer unused)
{
    (void) widget;
    printf("{\"kind\":\"user_closed_window\"}\n");
    stop(unused);
    return TRUE;
}

static gboolean hide_window(gpointer unused)
{
    (void) unused;
    gtk_widget_set_visible(window, FALSE);
    printf("{\"kind\":\"hidden\",\"time_us\":%ld}\n", g_get_monotonic_time());
    return G_SOURCE_REMOVE;
}

static void graphics_info(void)
{
    GError *error = NULL;
    GdkDisplay *display = gdk_display_get_default();
    GdkGLContext *context = gdk_display_create_gl_context(display, &error);
    if (context && gdk_gl_context_realize(context, &error)) {
        gdk_gl_context_make_current(context);
        fprintf(stderr, "GL renderer=%s vendor=%s version=%s\n", glGetString(GL_RENDERER),
                glGetString(GL_VENDOR), glGetString(GL_VERSION));
        printf("{\"kind\":\"graphics\",\"gl_renderer\":"); json_string((const char *) glGetString(GL_RENDERER));
        printf(",\"gl_vendor\":"); json_string((const char *) glGetString(GL_VENDOR));
        printf(",\"gl_version\":"); json_string((const char *) glGetString(GL_VERSION));
        EGLDisplay egl = eglGetCurrentDisplay();
        if (egl != EGL_NO_DISPLAY) {
            const char *ext = eglQueryString(egl, EGL_EXTENSIONS);
            fprintf(stderr, "EGL dma_buf_import=%d dma_buf_export=%d\n",
                    ext && strstr(ext, "EGL_EXT_image_dma_buf_import") != NULL,
                    ext && strstr(ext, "EGL_MESA_image_dma_buf_export") != NULL);
            printf(",\"egl_dmabuf_import\":%s,\"egl_dmabuf_export\":%s",
                    ext && strstr(ext, "EGL_EXT_image_dma_buf_import") ? "true" : "false",
                    ext && strstr(ext, "EGL_MESA_image_dma_buf_export") ? "true" : "false");
        }
        printf("}\n");
        gdk_gl_context_clear_current();
    } else fprintf(stderr, "GL unavailable: %s\n", error ? error->message : "no context");
    g_clear_error(&error);
    g_clear_object(&context);
    fprintf(stderr, "GDK DMA-BUF format/modifier pairs=%zu\n",
            gdk_dmabuf_formats_get_n_formats(gdk_display_get_dmabuf_formats(display)));
    printf("{\"kind\":\"dmabuf_capabilities\",\"format_modifier_pairs\":%zu}\n",
            gdk_dmabuf_formats_get_n_formats(gdk_display_get_dmabuf_formats(display)));
}

int main(int argc, char **argv)
{
    GOptionEntry options[] = {
        { "check", 0, 0, G_OPTION_ARG_NONE, &check_only, "Check system runtime versions and codec factories without opening a window", NULL },
        { "wait-for-video", 0, 0, G_OPTION_ARG_NONE, &wait_for_video, "Begin warmup only after local video starts; bound startup to 30 seconds", NULL },
        { "require-direct", 0, 0, G_OPTION_ARG_NONE, &require_direct, "Report unavailable instead of silently rendering in direct mode", NULL },
        { "window-width", 0, 0, G_OPTION_ARG_INT, &window_width, "Mapped window width (output remains 1920x1080)", "N" },
        { "window-height", 0, 0, G_OPTION_ARG_INT, &window_height, "Mapped window height", "N" },
        { "uri", 0, 0, G_OPTION_ARG_STRING, &uri, "Trusted local fixture URL", "URL" },
        { "mode", 0, 0, G_OPTION_ARG_STRING, &mode, "texture, direct, shm, render-shm", "MODE" },
        { "seconds", 0, 0, G_OPTION_ARG_INT, &seconds, "Measured duration", "N" },
        { "warmup", 0, 0, G_OPTION_ARG_INT, &warmup, "Warmup duration", "N" },
        { "audit-every", 0, 0, G_OPTION_ARG_INT, &audit_every, "GPU mode readback interval; 0 disables", "N" },
        { "shm", 0, 0, G_OPTION_ARG_STRING, &shm_path, "New private mapped file", "PATH" },
        { "ack-fd", 0, 0, G_OPTION_ARG_INT, &ack_fd, "Inherited SOCK_SEQPACKET consumer FD", "FD" },
        { "node", 0, 0, G_OPTION_ARG_STRING, &node_path, "Save first GSK node", "PATH" },
        { "png", 0, 0, G_OPTION_ARG_STRING, &png_path, "Save first frame (diagnostics only)", "PATH" },
        { "hide-after", 0, 0, G_OPTION_ARG_INT, &hide_after, "Hide after N measured seconds", "N" },
        { "software-webkit", 0, 0, G_OPTION_ARG_NONE, &software_webkit, "Disable WebKit acceleration, keep GSK selection", NULL },
        { "poll-hz", 0, 0, G_OPTION_ARG_INT, &poll_hz, "Poll cached widget content instead of invalidations", "N" },
        { "preview", 0, 0, G_OPTION_ARG_NONE, &preview, "Hand textures to a second native GTK window", NULL },
        { "software-web-process", 0, 0, G_OPTION_ARG_NONE, &software_web_process, "After GSK initialization select software GL for child processes (WSL diagnostic)", NULL },
        { NULL, 0, 0, 0, NULL, NULL, NULL }
    };
    GOptionContext *options_context = g_option_context_new(NULL);
    g_option_context_add_main_entries(options_context, options, NULL);
    GError *error = NULL;
    if (!g_option_context_parse(options_context, &argc, &argv, &error)) {
        fprintf(stderr, "%s\n", error->message); return 2;
    }
    if (check_only) { check_runtime(); return 0; }
    if (!uri || !mode || seconds < 1 || seconds > 300 || warmup < 0 || warmup > 60 || audit_every < 0 || poll_hz < 0 || poll_hz > 120 || window_width < 320 || window_width > WIDTH || window_height < 180 || window_height > HEIGHT ||
        (strcmp(mode, "texture") && strcmp(mode, "direct") && strcmp(mode, "shm") && strcmp(mode, "render-shm")))
        return 2;
    if (getuid() == 0) { fprintf(stderr, "Run as an ordinary desktop user with WebKit sandbox enabled.\n"); return 2; }
    shm_mode = !strcmp(mode, "shm") || !strcmp(mode, "render-shm");
    direct_mode = !strcmp(mode, "direct") || !strcmp(mode, "shm");
    if (shm_mode) {
        if (!shm_path || ack_fd < 0) return 2;
        int fd = open(shm_path, O_CREAT | O_EXCL | O_RDWR | O_CLOEXEC | O_NOFOLLOW, 0600);
        if (fd < 0 || ftruncate(fd, FRAME_BYTES * SLOTS) != 0) { perror("shared file"); return 2; }
        mapping = mmap(NULL, FRAME_BYTES * SLOTS, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
        close(fd);
        if (mapping == MAP_FAILED) { perror("mmap"); unlink(shm_path); return 2; }
    }
    audit = g_malloc(FRAME_BYTES);
    setvbuf(stdout, NULL, _IOLBF, 0);
    gtk_init();
    graphics_info();
    loop = g_main_loop_new(NULL, FALSE);
    window = gtk_window_new();
    gtk_window_set_title(GTK_WINDOW(window), "CrystalCast GTK frame experiment");
    gtk_window_set_decorated(GTK_WINDOW(window), TRUE);
    gtk_window_set_default_size(GTK_WINDOW(window), window_width, window_height);
    g_signal_connect(window, "close-request", G_CALLBACK(close_window), NULL);
    WebKitNetworkSession *session = webkit_network_session_new_ephemeral();
    view = g_object_new(WEBKIT_TYPE_WEB_VIEW, "network-session", session, NULL);
    g_object_unref(session);
    webkit_settings_set_media_playback_requires_user_gesture(webkit_web_view_get_settings(WEBKIT_WEB_VIEW(view)), FALSE);
    if (software_webkit)
        webkit_settings_set_hardware_acceleration_policy(webkit_web_view_get_settings(WEBKIT_WEB_VIEW(view)), WEBKIT_HARDWARE_ACCELERATION_POLICY_NEVER);
    WebKitUserContentManager *manager = webkit_web_view_get_user_content_manager(WEBKIT_WEB_VIEW(view));
    webkit_user_content_manager_register_script_message_handler(manager, "probe", NULL);
    g_signal_connect(manager, "script-message-received::probe", G_CALLBACK(media_message), NULL);
    webkit_user_content_manager_register_script_message_handler(manager, "probeReady", NULL);
    g_signal_connect(manager, "script-message-received::probeReady", G_CALLBACK(ready_message), NULL);
    if (window_width < WIDTH || window_height < HEIGHT) {
        /* Keep the browser allocation at full test resolution. Only its mapped
         * desktop viewport is smaller; capture observes the WebView itself. */
        GtkWidget *viewport = gtk_scrolled_window_new();
        /* NEVER propagates the child's minimum size; EXTERNAL actually clips. */
        gtk_scrolled_window_set_policy(GTK_SCROLLED_WINDOW(viewport), GTK_POLICY_EXTERNAL, GTK_POLICY_EXTERNAL);
        gtk_widget_set_size_request(view, WIDTH, HEIGHT);
        gtk_scrolled_window_set_child(GTK_SCROLLED_WINDOW(viewport), view);
        gtk_window_set_child(GTK_WINDOW(window), viewport);
    } else gtk_window_set_child(GTK_WINDOW(window), view);
    paintable = gtk_widget_paintable_new(view);
    g_signal_connect(paintable, "invalidate-contents", G_CALLBACK(invalidated), NULL);
    started = g_get_monotonic_time();
    stop_source = g_timeout_add(wait_for_video ? 30000 : (warmup + seconds) * 1000,
                                wait_for_video ? startup_timeout : stop, NULL);
    if (!wait_for_video && hide_after > 0) g_timeout_add((warmup + hide_after) * 1000, hide_window, NULL);
    gtk_window_present(GTK_WINDOW(window));
    if (preview) {
        preview_window = gtk_window_new();
        gtk_window_set_title(GTK_WINDOW(preview_window), "Native texture consumer (same process)");
        gtk_window_set_default_size(GTK_WINDOW(preview_window), 640, 360);
        picture = gtk_picture_new();
        gtk_window_set_child(GTK_WINDOW(preview_window), picture);
        g_signal_connect(preview_window, "close-request", G_CALLBACK(close_window), NULL);
        gtk_window_present(GTK_WINDOW(preview_window));
    }
    printf("{\"kind\":\"start\",\"time_us\":%ld,\"warmup\":%d,\"seconds\":%d,\"gtk\":\"%d.%d.%d\","
           "\"webkit\":\"%d.%d.%d\",\"renderer\":\"%s\",\"display\":\"%s\"}\n",
           started, warmup, seconds, gtk_get_major_version(), gtk_get_minor_version(), gtk_get_micro_version(),
           webkit_get_major_version(), webkit_get_minor_version(), webkit_get_micro_version(),
           G_OBJECT_TYPE_NAME(gtk_native_get_renderer(GTK_NATIVE(window))), G_OBJECT_TYPE_NAME(gdk_display_get_default()));
    if (software_web_process) {
        /* GDK's EGL display and the window's GSK context are already realized.
         * Only subsequently launched processes should select software Mesa.
         * This measures a mixed WSL pipeline, not browser DMA-BUF rendering. */
        g_unsetenv("MESA_LOADER_DRIVER_OVERRIDE");
        g_setenv("GALLIUM_DRIVER", "llvmpipe", TRUE);
        g_setenv("LIBGL_ALWAYS_SOFTWARE", "1", TRUE);
    }
    webkit_web_view_load_uri(WEBKIT_WEB_VIEW(view), uri);
    guint poll_source = poll_hz ? g_timeout_add(1, poll_capture, NULL) : 0;
    g_main_loop_run(loop);
    if (poll_source) g_source_remove(poll_source);
    if (pending) g_source_remove(pending);
    printf("{\"kind\":\"end\",\"time_us\":%ld,\"frames\":%lu,\"invalidations\":%lu,\"drops\":%lu,\"empty\":%lu}\n",
           g_get_monotonic_time(), sequence, invalidations, drops, empty);
    g_object_unref(paintable);
    if (preview_window) gtk_window_destroy(GTK_WINDOW(preview_window));
    gtk_window_destroy(GTK_WINDOW(window));
    g_main_loop_unref(loop);
    if (mapping) { munmap(mapping, FRAME_BYTES * SLOTS); unlink(shm_path); }
    if (ack_fd >= 0) close(ack_fd);
    g_free(audit);
    g_option_context_free(options_context);
    return unavailable ? 77 : failed || !sequence ? 1 : 0;
}
