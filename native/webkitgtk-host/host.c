/* CrystalCast native WebKitGTK browser host (experimental, Linux + Wine only).
 *
 * Started by the CrystalCast plugin from inside Wine. It connects back to the
 * plugin over an authenticated loopback TCP connection, renders the plugin's
 * provider player page in a mapped GTK window, and copies each new browser frame
 * into one of three leased BGRA slots in a shared file the plugin maps through
 * Wine. The plugin acknowledges a slot before it is reused.
 *
 * Wire format, both directions: little-endian uint32 byte length, then UTF-8 JSON.
 */
#define _GNU_SOURCE
#include <gtk/gtk.h>
#include <webkit/webkit.h>
#include <glib-unix.h>
#include <arpa/inet.h>
#include <errno.h>
#include <fcntl.h>
#include <netinet/in.h>
#include <netinet/tcp.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <sys/socket.h>
#include <unistd.h>

enum { PROTOCOL = 1, SLOTS = 3, MAX_MESSAGE = 1024 * 1024, MIN_W = 320, MAX_W = 3840, MIN_H = 180, MAX_H = 2160 };
enum { CAPTURE_W = 480, CAPTURE_H = 270, CONTROLS_W = 1280, CONTROLS_H = 720 };

static const char bridge_script[] =
    "(function () {\n"
    "  if (window.chrome && window.chrome.webview) return;\n"
    "  var handler = window.webkit && window.webkit.messageHandlers && window.webkit.messageHandlers.crystalcast;\n"
    "  if (!handler) return;\n"
    "  var listeners = [];\n"
    "  window.chrome = window.chrome || {};\n"
    "  window.chrome.webview = {\n"
    "    postMessage: function (message) { handler.postMessage(JSON.stringify(message)); },\n"
    "    addEventListener: function (type, listener) { if (type === 'message' && typeof listener === 'function') listeners.push(listener); },\n"
    "    removeEventListener: function (type, listener) { var i = listeners.indexOf(listener); if (i >= 0) listeners.splice(i, 1); }\n"
    "  };\n"
    "  Object.defineProperty(window, '__crystalCastDeliver', { value: function (data) {\n"
    "    listeners.slice().forEach(function (listener) { try { listener({ data: data }); } catch (e) {} });\n"
    "  } });\n"
    "})();\n";

static int width = 1280, height = 720, fps_limit = 60;
static int sock = -1;
static GByteArray *inbox;
static GMainLoop *loop;
static GtkWidget *window, *scroller, *view;
static GdkPaintable *paintable;
static JSCContext *json_context;
static unsigned char *mapping;
static size_t frame_bytes;
static char *mapping_path, *page_url, *page_origin_pattern, *page_html;
static uint64_t sequence, leases[SLOTS], busy_drops;
static gboolean capture_enabled, page_ready, controls_visible, graphics_reported, mapping_unlinked;
static guint pending_capture;
static gint64 last_capture_us;
static int exit_code;
static gint64 crash_times[3];
static unsigned crash_count;

static void quit(int code)
{
    if (!exit_code) exit_code = code;
    if (loop) g_main_loop_quit(loop);
}

static void append_json_string(GString *out, const char *value)
{
    g_string_append_c(out, '"');
    for (const unsigned char *p = (const unsigned char *) (value ? value : ""); *p; ++p) {
        if (*p == '"' || *p == '\\') g_string_append_printf(out, "\\%c", *p);
        else if (*p < 0x20) g_string_append_printf(out, "\\u%04x", *p);
        else g_string_append_c(out, (char) *p);
    }
    g_string_append_c(out, '"');
}

static gboolean send_all(const void *data, size_t size)
{
    const char *p = data;
    while (size > 0) {
        ssize_t sent = send(sock, p, size, MSG_NOSIGNAL);
        if (sent < 0 && errno == EINTR) continue;
        if (sent <= 0) return FALSE;
        p += sent;
        size -= (size_t) sent;
    }
    return TRUE;
}

/* Takes ownership of message. */
static void send_message(GString *message)
{
    if (sock >= 0 && message->len <= MAX_MESSAGE) {
        uint32_t length = GUINT32_TO_LE((uint32_t) message->len);
        if (!send_all(&length, sizeof length) || !send_all(message->str, message->len)) {
            fprintf(stderr, "crystalcast-webkit-host: plugin connection lost while sending\n");
            quit(1);
        }
    }
    g_string_free(message, TRUE);
}

static void send_status(const char *type, const char *text)
{
    GString *m = g_string_new("{\"type\":");
    append_json_string(m, type);
    g_string_append(m, ",\"message\":");
    append_json_string(m, text);
    g_string_append_c(m, '}');
    send_message(m);
}

static void fatal(const char *text)
{
    fprintf(stderr, "crystalcast-webkit-host: %s\n", text);
    send_status("fatal", text);
    quit(1);
}

/* Accept only a texture covering the exact viewport, with nothing that changes its pixels. */
static GdkTexture *exact_texture(GskRenderNode *node)
{
    graphene_rect_t viewport = GRAPHENE_RECT_INIT(0, 0, width, height), bounds;
    if (gsk_render_node_get_node_type(node) == GSK_CLIP_NODE) {
        if (!graphene_rect_contains_rect(gsk_clip_node_get_clip(node), &viewport)) return NULL;
        return exact_texture(gsk_clip_node_get_child(node));
    }
    if (gsk_render_node_get_node_type(node) != GSK_TEXTURE_NODE) return NULL;
    gsk_render_node_get_bounds(node, &bounds);
    GdkTexture *texture = gsk_texture_node_get_texture(node);
    if (!graphene_rect_equal(&bounds, &viewport) ||
        gdk_texture_get_width(texture) != width || gdk_texture_get_height(texture) != height)
        return NULL;
    return texture;
}

static int free_slot(void)
{
    for (int i = 0; i < SLOTS; ++i) if (!leases[i]) return i;
    return -1;
}

static gboolean capture(gpointer unused);

static void schedule_capture(guint delay_ms)
{
    if (pending_capture) return;
    pending_capture = delay_ms
        ? g_timeout_add_full(G_PRIORITY_HIGH, delay_ms, capture, NULL, NULL)
        : g_idle_add_full(G_PRIORITY_HIGH, capture, NULL, NULL);
}

static gboolean capture(gpointer unused)
{
    (void) unused;
    pending_capture = 0;
    if (!capture_enabled || !mapping || !page_ready) return G_SOURCE_REMOVE;
    gint64 begin = g_get_monotonic_time();
    gint64 interval = G_USEC_PER_SEC / (fps_limit > 0 ? fps_limit : 60);
    /* Small slack so a 60 Hz source is not rounded down to every second frame. */
    if (last_capture_us && begin - last_capture_us < interval - 2000) {
        schedule_capture((guint) ((interval - (begin - last_capture_us)) / 1000) + 1);
        return G_SOURCE_REMOVE;
    }
    int slot = free_slot();
    if (slot < 0) { ++busy_drops; return G_SOURCE_REMOVE; }

    GtkSnapshot *snapshot = gtk_snapshot_new();
    gdk_paintable_snapshot(paintable, GDK_SNAPSHOT(snapshot), width, height);
    GskRenderNode *node = gtk_snapshot_free_to_node(snapshot);
    if (!node) return G_SOURCE_REMOVE;
    GdkTexture *source = exact_texture(node);
    GdkTexture *texture;
    if (source) texture = g_object_ref(source);
    else {
        graphene_rect_t viewport = GRAPHENE_RECT_INIT(0, 0, width, height);
        texture = gsk_renderer_render_texture(gtk_native_get_renderer(GTK_NATIVE(window)), node, &viewport);
    }
    if (!texture) { gsk_render_node_unref(node); fatal("GTK did not render a browser texture"); return G_SOURCE_REMOVE; }
    if (!graphics_reported) {
        GString *m = g_string_new("{\"type\":\"graphics\",\"source\":");
        append_json_string(m, source ? G_OBJECT_TYPE_NAME(source) : "composite");
        g_string_append(m, ",\"output\":");
        append_json_string(m, G_OBJECT_TYPE_NAME(texture));
        g_string_append_c(m, '}');
        send_message(m);
        graphics_reported = TRUE;
    }
    gdk_texture_download(texture, mapping + frame_bytes * (size_t) slot, (gsize) width * 4);
    g_object_unref(texture);
    gsk_render_node_unref(node);
    gint64 end = g_get_monotonic_time();
    last_capture_us = begin;
    leases[slot] = ++sequence;
    GString *m = g_string_new(NULL);
    g_string_printf(m, "{\"type\":\"frame\",\"seq\":%" G_GUINT64_FORMAT ",\"slot\":%d,\"extract_us\":%" G_GINT64_FORMAT
                    ",\"busy_drops\":%" G_GUINT64_FORMAT "}", sequence, slot, end - begin, busy_drops);
    send_message(m);
    return G_SOURCE_REMOVE;
}

static void invalidated(GdkPaintable *p, gpointer unused)
{
    (void) p; (void) unused;
    /* GtkWidgetPaintable publishes its new image in a high-priority idle. */
    schedule_capture(0);
}

static void set_controls_visible(gboolean visible)
{
    controls_visible = visible;
    /* GTK 4 only applies a new default size when the window is shown again. */
    if (gtk_widget_get_mapped(window)) gtk_widget_set_visible(window, FALSE);
    gtk_scrolled_window_set_policy(GTK_SCROLLED_WINDOW(scroller),
        visible ? GTK_POLICY_AUTOMATIC : GTK_POLICY_EXTERNAL, visible ? GTK_POLICY_AUTOMATIC : GTK_POLICY_EXTERNAL);
    gtk_window_set_default_size(GTK_WINDOW(window),
        visible ? MIN(width, CONTROLS_W) : MIN(width, CAPTURE_W), visible ? MIN(height, CONTROLS_H) : MIN(height, CAPTURE_H));
    gtk_window_set_title(GTK_WINDOW(window), visible
        ? "CrystalCast browser controls (close this window to return to the game)"
        : "CrystalCast browser (keep this window open)");
    /* Presenting activates the window. Only do that when the user asked for controls,
     * so opening a screen does not take focus from (or minimize) a fullscreen game. */
    if (visible) gtk_window_present(GTK_WINDOW(window));
    else gtk_widget_set_visible(window, TRUE);
    GString *m = g_string_new(visible ? "{\"type\":\"controls\",\"visible\":true}" : "{\"type\":\"controls\",\"visible\":false}");
    send_message(m);
}

static gboolean close_requested(GtkWindow *w, gpointer unused)
{
    (void) w; (void) unused;
    /* Closing the window would stop capture; it only leaves controls mode. */
    if (controls_visible) set_controls_visible(FALSE);
    else send_status("status", "browser window must stay open while the screen is active");
    return TRUE;
}

static void page_message(WebKitUserContentManager *manager, JSCValue *value, gpointer unused)
{
    (void) manager; (void) unused;
    char *json = jsc_value_is_string(value) ? jsc_value_to_string(value) : jsc_value_to_json(value, 0);
    if (!json) return;
    if (strlen(json) <= 64 * 1024) {
        GString *m = g_string_new("{\"type\":\"page\",\"data\":");
        append_json_string(m, json);
        g_string_append_c(m, '}');
        send_message(m);
    }
    g_free(json);
}

static void load_page(const char *html)
{
    WebKitUserContentManager *manager = webkit_web_view_get_user_content_manager(WEBKIT_WEB_VIEW(view));
    webkit_user_content_manager_remove_all_scripts(manager);
    const char *allow[] = { page_origin_pattern, NULL };
    WebKitUserScript *script = webkit_user_script_new(bridge_script, WEBKIT_USER_CONTENT_INJECT_TOP_FRAME,
        WEBKIT_USER_SCRIPT_INJECT_AT_DOCUMENT_START, allow, NULL);
    webkit_user_content_manager_add_script(manager, script);
    webkit_user_script_unref(script);
    page_ready = FALSE;
    send_message(g_string_new("{\"type\":\"loading\"}"));
    webkit_web_view_load_html(WEBKIT_WEB_VIEW(view), html, page_url);
}

static gboolean reload_player(gpointer unused)
{
    (void) unused;
    if (page_html) load_page(page_html);
    return G_SOURCE_REMOVE;
}

static void uri_changed(GObject *object, GParamSpec *spec, gpointer unused)
{
    (void) object; (void) spec; (void) unused;
    const char *uri = webkit_web_view_get_uri(WEBKIT_WEB_VIEW(view));
    if (!page_url || !uri || !*uri || !strcmp(uri, page_url)) return;
    /* The main frame may only show the generated player document. */
    webkit_web_view_stop_loading(WEBKIT_WEB_VIEW(view));
    send_status("status", "blocked browser navigation outside the provider player");
    g_idle_add(reload_player, NULL);
}

static void load_changed(WebKitWebView *web_view, WebKitLoadEvent event, gpointer unused)
{
    (void) web_view; (void) unused;
    if (event == WEBKIT_LOAD_FINISHED) {
        page_ready = TRUE;
        send_status("status", "player page loaded");
        schedule_capture(0);
    }
}

static gboolean load_failed(WebKitWebView *web_view, WebKitLoadEvent event, char *uri, GError *error, gpointer unused)
{
    (void) web_view; (void) event; (void) uri; (void) unused;
    fprintf(stderr, "crystalcast-webkit-host: page load failed: %s\n", error ? error->message : "unknown error");
    send_status("status", error ? error->message : "page load failed");
    return FALSE;
}

static gboolean decide_policy(WebKitWebView *web_view, WebKitPolicyDecision *decision, WebKitPolicyDecisionType type, gpointer unused)
{
    (void) web_view; (void) unused;
    if (type == WEBKIT_POLICY_DECISION_TYPE_NEW_WINDOW_ACTION) { webkit_policy_decision_ignore(decision); return TRUE; }
    if (type == WEBKIT_POLICY_DECISION_TYPE_RESPONSE &&
        !webkit_response_policy_decision_is_mime_type_supported(WEBKIT_RESPONSE_POLICY_DECISION(decision))) {
        webkit_policy_decision_ignore(decision); /* no downloads */
        return TRUE;
    }
    return FALSE;
}

static gboolean permission_requested(WebKitWebView *web_view, WebKitPermissionRequest *request, gpointer unused)
{
    (void) web_view; (void) unused;
    webkit_permission_request_deny(request);
    return TRUE;
}

static void download_started(WebKitNetworkSession *session, WebKitDownload *download, gpointer unused)
{
    (void) session; (void) unused;
    webkit_download_cancel(download);
}

static gboolean web_process_terminated(WebKitWebView *web_view, WebKitWebProcessTerminationReason reason, gpointer unused)
{
    (void) web_view; (void) unused;
    const char *what = reason == WEBKIT_WEB_PROCESS_CRASHED ? "crashed"
        : reason == WEBKIT_WEB_PROCESS_EXCEEDED_MEMORY_LIMIT ? "exceeded its memory limit" : "ended";
    fprintf(stderr, "crystalcast-webkit-host: browser web process %s\n", what);
    /* Reload after an occasional crash, but stop a crash loop instead of spinning. */
    gint64 now = g_get_monotonic_time();
    crash_times[crash_count++ % G_N_ELEMENTS(crash_times)] = now;
    if (crash_count >= G_N_ELEMENTS(crash_times) &&
        now - crash_times[crash_count % G_N_ELEMENTS(crash_times)] < 60 * G_USEC_PER_SEC) {
        char *message = g_strdup_printf("the browser web process %s three times within a minute; giving up", what);
        fatal(message);
        g_free(message);
        return TRUE;
    }
    char *message = g_strdup_printf("browser web process %s; reloading the player", what);
    send_status("status", message);
    g_free(message);
    g_idle_add(reload_player, NULL);
    return TRUE;
}

static char *string_property(JSCValue *object, const char *name)
{
    JSCValue *value = jsc_value_object_get_property(object, name);
    char *result = jsc_value_is_string(value) ? jsc_value_to_string(value) : NULL;
    g_object_unref(value);
    return result;
}

static double number_property(JSCValue *object, const char *name, double fallback)
{
    JSCValue *value = jsc_value_object_get_property(object, name);
    double result = jsc_value_is_number(value) ? jsc_value_to_double(value) : fallback;
    g_object_unref(value);
    return result;
}

static gboolean bool_property(JSCValue *object, const char *name)
{
    JSCValue *value = jsc_value_object_get_property(object, name);
    gboolean result = jsc_value_is_boolean(value) && jsc_value_to_boolean(value);
    g_object_unref(value);
    return result;
}

static char *origin_pattern(const char *url)
{
    /* "https://host/path" -> "https://host/" + "*" */
    if (!g_str_has_prefix(url, "https://")) return NULL;
    const char *slash = strchr(url + 8, '/');
    if (!slash || slash == url + 8) return NULL;
    return g_strdup_printf("%.*s/*", (int) (slash - url), url);
}

static void handle_message(const char *json)
{
    JSCValue *root = jsc_value_new_from_json(json_context, json);
    if (!root || !jsc_value_is_object(root)) {
        if (root) g_object_unref(root);
        fatal("malformed plugin message");
        return;
    }
    char *type = string_property(root, "type");
    if (!type) {
    } else if (!strcmp(type, "load")) {
        char *url = string_property(root, "url"), *html = string_property(root, "html");
        char *pattern = url ? origin_pattern(url) : NULL;
        if (!url || !html || !pattern) {
            g_free(url); g_free(html); g_free(pattern);
            fatal("invalid player page");
        } else {
            g_free(page_url); g_free(page_html); g_free(page_origin_pattern);
            page_url = url; page_html = html; page_origin_pattern = pattern;
            load_page(page_html);
        }
    } else if (!strcmp(type, "post")) {
        /* Re-serialize so only a JSON value, never script text, reaches the page. */
        JSCValue *message = jsc_value_object_get_property(root, "message");
        char *literal = jsc_value_is_object(message) ? jsc_value_to_json(message, 0) : NULL;
        if (literal) {
            char *script = g_strdup_printf("window.__crystalCastDeliver && window.__crystalCastDeliver(%s);", literal);
            webkit_web_view_evaluate_javascript(WEBKIT_WEB_VIEW(view), script, -1, NULL, NULL, NULL, NULL, NULL);
            g_free(script);
            g_free(literal);
        }
        g_object_unref(message);
    } else if (!strcmp(type, "ack")) {
        double seq = number_property(root, "seq", 0);
        gboolean found = FALSE;
        for (int i = 0; i < SLOTS; ++i) {
            if (leases[i] && (double) leases[i] == seq) { leases[i] = 0; found = TRUE; break; }
        }
        if (!found) fatal("unknown or duplicate frame acknowledgement");
        else if (capture_enabled) schedule_capture(0);
    } else if (!strcmp(type, "mapped")) {
        /* The plugin holds its own mapping now; nothing else may open the file. */
        if (mapping_path && !mapping_unlinked) { unlink(mapping_path); mapping_unlinked = TRUE; }
    } else if (!strcmp(type, "capture")) {
        capture_enabled = bool_property(root, "enabled");
        if (capture_enabled) schedule_capture(0);
    } else if (!strcmp(type, "muted")) {
        webkit_web_view_set_is_muted(WEBKIT_WEB_VIEW(view), bool_property(root, "value"));
    } else if (!strcmp(type, "fps")) {
        double fps = number_property(root, "value", 60);
        fps_limit = fps >= 1 && fps <= 120 ? (int) (fps + 0.5) : 60;
    } else if (!strcmp(type, "controls")) {
        set_controls_visible(bool_property(root, "visible"));
    } else if (!strcmp(type, "shutdown")) {
        quit(0);
    }
    g_free(type);
    g_object_unref(root);
}

static gboolean socket_readable(gint fd, GIOCondition condition, gpointer unused)
{
    (void) unused;
    unsigned char buffer[65536];
    ssize_t size = recv(fd, buffer, sizeof buffer, 0);
    (void) condition;
    if (size < 0 && (errno == EINTR || errno == EAGAIN)) return G_SOURCE_CONTINUE;
    if (size <= 0) {
        quit(0); /* The plugin closed the connection: the screen or the game ended. */
        return G_SOURCE_REMOVE;
    }
    g_byte_array_append(inbox, buffer, (guint) size);
    while (inbox->len >= 4) {
        uint32_t length;
        memcpy(&length, inbox->data, sizeof length);
        length = GUINT32_FROM_LE(length);
        if (length == 0 || length > 64 * 1024 * 1024) { fatal("invalid plugin message length"); return G_SOURCE_REMOVE; }
        if (inbox->len < 4 + length) break;
        char *json = g_strndup((const char *) inbox->data + 4, length);
        g_byte_array_remove_range(inbox, 0, 4 + length);
        handle_message(json);
        g_free(json);
        if (exit_code) return G_SOURCE_REMOVE;
    }
    return G_SOURCE_CONTINUE;
}

static int connect_plugin(void)
{
    const char *port_text = g_getenv("CRYSTALCAST_HOST_PORT");
    char *end = NULL;
    long port = port_text ? strtol(port_text, &end, 10) : 0;
    if (!port_text || *end || port < 1 || port > 65535) return -1;
    int fd = socket(AF_INET, SOCK_STREAM | SOCK_CLOEXEC, 0);
    if (fd < 0) return -1;
    struct sockaddr_in address = { .sin_family = AF_INET, .sin_port = htons((uint16_t) port) };
    address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    if (connect(fd, (struct sockaddr *) &address, sizeof address) != 0) { close(fd); return -1; }
    int one = 1;
    setsockopt(fd, IPPROTO_TCP, TCP_NODELAY, &one, sizeof one);
    /* If the game stops reading (hung or paused in a debugger), give up instead of
     * blocking the browser forever; the plugin reports the lost connection. */
    struct timeval timeout = { .tv_sec = 5 };
    setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, &timeout, sizeof timeout);
    return fd;
}

static gboolean create_mapping(void)
{
    frame_bytes = (size_t) width * 4 * (size_t) height;
    mapping_path = g_build_filename(g_get_user_runtime_dir(), "crystalcast-webkit-XXXXXX", NULL);
    int fd = mkostemp(mapping_path, O_CLOEXEC);
    if (fd < 0) {
        g_free(mapping_path);
        mapping_path = g_build_filename(g_get_tmp_dir(), "crystalcast-webkit-XXXXXX", NULL);
        fd = mkostemp(mapping_path, O_CLOEXEC);
    }
    if (fd < 0) return FALSE;
    fchmod(fd, 0600);
    if (ftruncate(fd, (off_t) (frame_bytes * SLOTS)) != 0) { close(fd); unlink(mapping_path); return FALSE; }
    mapping = mmap(NULL, frame_bytes * SLOTS, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
    close(fd);
    if (mapping == MAP_FAILED) { mapping = NULL; unlink(mapping_path); return FALSE; }
    return TRUE;
}

int main(int argc, char **argv)
{
    GOptionEntry options[] = {
        { "width", 0, 0, G_OPTION_ARG_INT, &width, "Browser width", "N" },
        { "height", 0, 0, G_OPTION_ARG_INT, &height, "Browser height", "N" },
        { "fps", 0, 0, G_OPTION_ARG_INT, &fps_limit, "Maximum captured frames per second", "N" },
        { NULL, 0, 0, 0, NULL, NULL, NULL }
    };
    GOptionContext *context = g_option_context_new("- CrystalCast WebKitGTK host");
    g_option_context_add_main_entries(context, options, NULL);
    GError *error = NULL;
    if (!g_option_context_parse(context, &argc, &argv, &error) ||
        width < MIN_W || width > MAX_W || height < MIN_H || height > MAX_H || fps_limit < 1 || fps_limit > 120) {
        fprintf(stderr, "crystalcast-webkit-host: invalid arguments%s%s\n", error ? ": " : "", error ? error->message : "");
        return 2;
    }
    g_option_context_free(context);
    if (getuid() == 0) { fprintf(stderr, "crystalcast-webkit-host: refusing to run as root\n"); return 2; }

    /* Read and remove the secrets so browser child processes never inherit them. */
    char *token = g_strdup(g_getenv("CRYSTALCAST_HOST_TOKEN"));
    sock = connect_plugin();
    g_unsetenv("CRYSTALCAST_HOST_TOKEN");
    g_unsetenv("CRYSTALCAST_HOST_PORT");
    if (!token || strlen(token) != 64) { fprintf(stderr, "crystalcast-webkit-host: missing session token\n"); return 2; }
    if (sock < 0) { fprintf(stderr, "crystalcast-webkit-host: could not connect to the plugin: %s\n", g_strerror(errno)); return 3; }
    setvbuf(stderr, NULL, _IOLBF, 0);

    GString *hello = g_string_new(NULL);
    g_string_printf(hello, "{\"type\":\"hello\",\"protocol\":%d,\"pid\":%d,\"gtk\":\"%u.%u.%u\",\"webkit\":\"%u.%u.%u\",\"token\":",
        PROTOCOL, (int) getpid(), gtk_get_major_version(), gtk_get_minor_version(), gtk_get_micro_version(),
        webkit_get_major_version(), webkit_get_minor_version(), webkit_get_micro_version());
    append_json_string(hello, token);
    g_string_append_c(hello, '}');
    send_message(hello);
    g_free(token);

    if (!gtk_init_check()) {
        fatal("GTK could not open the Linux desktop display (is DISPLAY or WAYLAND_DISPLAY available to Wine?)");
        return 4;
    }
    if (!create_mapping()) { fatal("could not create the shared frame file"); return 5; }

    loop = g_main_loop_new(NULL, FALSE);
    inbox = g_byte_array_new();

    window = gtk_window_new();
    g_signal_connect(window, "close-request", G_CALLBACK(close_requested), NULL);
    WebKitNetworkSession *session = webkit_network_session_new_ephemeral();
    g_signal_connect(session, "download-started", G_CALLBACK(download_started), NULL);
    WebKitWebsitePolicies *policies = webkit_website_policies_new_with_policies("autoplay", WEBKIT_AUTOPLAY_ALLOW, NULL);
    view = g_object_new(WEBKIT_TYPE_WEB_VIEW, "network-session", session, "website-policies", policies, NULL);
    g_object_unref(policies);
    g_object_unref(session);
    WebKitSettings *settings = webkit_web_view_get_settings(WEBKIT_WEB_VIEW(view));
    webkit_settings_set_media_playback_requires_user_gesture(settings, FALSE);
    webkit_settings_set_javascript_can_open_windows_automatically(settings, FALSE);
    webkit_settings_set_enable_developer_extras(settings, FALSE);
    GdkRGBA black = { 0, 0, 0, 1 };
    webkit_web_view_set_background_color(WEBKIT_WEB_VIEW(view), &black);
    g_signal_connect(view, "notify::uri", G_CALLBACK(uri_changed), NULL);
    g_signal_connect(view, "load-changed", G_CALLBACK(load_changed), NULL);
    g_signal_connect(view, "load-failed", G_CALLBACK(load_failed), NULL);
    g_signal_connect(view, "decide-policy", G_CALLBACK(decide_policy), NULL);
    g_signal_connect(view, "permission-request", G_CALLBACK(permission_requested), NULL);
    g_signal_connect(view, "web-process-terminated", G_CALLBACK(web_process_terminated), NULL);
    /* JSON parsing context. It must be created after the first web view: a JavaScriptCore
     * VM created earlier in this UI process stops WebKit from ever loading a page. */
    json_context = jsc_context_new();
    WebKitUserContentManager *manager = webkit_web_view_get_user_content_manager(WEBKIT_WEB_VIEW(view));
    webkit_user_content_manager_register_script_message_handler(manager, "crystalcast", NULL);
    g_signal_connect(manager, "script-message-received::crystalcast", G_CALLBACK(page_message), NULL);

    /* The browser keeps its full capture size; the mapped window only shows part of it. */
    scroller = gtk_scrolled_window_new();
    gtk_widget_set_size_request(view, width, height);
    gtk_scrolled_window_set_child(GTK_SCROLLED_WINDOW(scroller), view);
    gtk_window_set_child(GTK_WINDOW(window), scroller);
    paintable = gtk_widget_paintable_new(view);
    g_signal_connect(paintable, "invalidate-contents", G_CALLBACK(invalidated), NULL);
    set_controls_visible(FALSE);

    fprintf(stderr, "crystalcast-webkit-host: GTK %u.%u.%u, WebKitGTK %u.%u.%u, %s on %s, %dx%d at up to %d fps\n",
        gtk_get_major_version(), gtk_get_minor_version(), gtk_get_micro_version(),
        webkit_get_major_version(), webkit_get_minor_version(), webkit_get_micro_version(),
        G_OBJECT_TYPE_NAME(gtk_native_get_renderer(GTK_NATIVE(window))), G_OBJECT_TYPE_NAME(gdk_display_get_default()),
        width, height, fps_limit);
    GString *ready = g_string_new(NULL);
    g_string_printf(ready, "{\"type\":\"ready\",\"width\":%d,\"height\":%d,\"stride\":%d,\"slots\":%d,\"renderer\":",
        width, height, width * 4, SLOTS);
    append_json_string(ready, G_OBJECT_TYPE_NAME(gtk_native_get_renderer(GTK_NATIVE(window))));
    g_string_append(ready, ",\"display\":");
    append_json_string(ready, G_OBJECT_TYPE_NAME(gdk_display_get_default()));
    g_string_append(ready, ",\"mapping\":");
    append_json_string(ready, mapping_path);
    g_string_append_c(ready, '}');
    send_message(ready);

    g_unix_fd_add(sock, G_IO_IN | G_IO_HUP | G_IO_ERR, socket_readable, NULL);
    if (!exit_code) g_main_loop_run(loop);

    if (pending_capture) g_source_remove(pending_capture);
    g_object_unref(paintable);
    gtk_window_destroy(GTK_WINDOW(window));
    if (mapping) munmap(mapping, frame_bytes * SLOTS);
    if (mapping_path && !mapping_unlinked) unlink(mapping_path);
    close(sock);
    g_object_unref(json_context);
    g_byte_array_unref(inbox);
    g_main_loop_unref(loop);
    fprintf(stderr, "crystalcast-webkit-host: exiting (code %d, %" G_GUINT64_FORMAT " frames)\n", exit_code, sequence);
    return exit_code;
}
