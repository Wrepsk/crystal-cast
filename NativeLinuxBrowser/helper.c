#include "bridge.h"
#include <glib-unix.h>
#include <errno.h>
#include <gst/gst.h>
#include <jsc/jsc.h>
#include <signal.h>
#include <stdio.h>
#include <string.h>
#include <sys/prctl.h>
#include <sys/stat.h>
#include <sys/wait.h>
#include <unistd.h>
#include <wpe/webkit.h>
#include <wpe/wpe-platform.h>

typedef struct {
    GMainLoop *loop;
    Bridge *bridge;
    WebKitWebView *web;
    WebKitUserContentManager *manager;
    char *expected_uri;
    gboolean generic, locked_navigation, loaded, acceleration_reported, capture_enabled, stopping;
    unsigned width, height;
    double fps;
    gint64 next_frame, frames, export_us;
} App;

/* Adapt the existing CrystalCast player protocol without rewriting providers.
 * Injected only into the top frame; permission and navigation policies remain
 * native decisions. Generic Web is intentionally an untrusted page. */
static const char *host_bridge =
    "(()=>{if(window.top!==window)return;const listeners=[];"
    "window.chrome=window.chrome||{};window.chrome.webview={"
    "postMessage:m=>window.webkit.messageHandlers.crystalcast.postMessage(JSON.stringify(m)),"
    "addEventListener:(t,f)=>{if(t==='message')listeners.push(f)}};"
    "window.__crystalcastNativeDispatch=m=>listeners.forEach(f=>f({data:m}));})();";

static const char *member_string(JsonObject *object, const char *name)
{
    JsonNode *node = json_object_get_member(object, name);
    return node && JSON_NODE_HOLDS_VALUE(node) && json_node_get_value_type(node) == G_TYPE_STRING
        ? json_node_get_string(node) : "";
}
static void send_object(App *app, JsonBuilder *builder)
{
    JsonGenerator *generator = json_generator_new();
    JsonNode *root = json_builder_get_root(builder);
    json_generator_set_root(generator, root);
    char *json = json_generator_to_data(generator, NULL);
    bridge_json(app->bridge, json);
    g_free(json); json_node_free(root); g_object_unref(generator); g_object_unref(builder);
}
static void status(App *app, const char *type, const char *message)
{
    JsonBuilder *builder = json_builder_new();
    json_builder_begin_object(builder);
    json_builder_set_member_name(builder, "type"); json_builder_add_string_value(builder, type);
    json_builder_set_member_name(builder, "message"); json_builder_add_string_value(builder, message);
    json_builder_end_object(builder);
    send_object(app, builder);
}
static gboolean quit(gpointer data)
{
    g_main_loop_quit(((App *)data)->loop);
    return G_SOURCE_REMOVE;
}
static void peer_disconnected(void *data) { quit(data); }
static gboolean signal_quit(gpointer data) { quit(data); return G_SOURCE_CONTINUE; }

static void reap_children(void)
{
    /* WebKit closes auxiliary processes asynchronously. Adopt grandchildren so
     * bubblewrap exits cannot leave zombies in a launcher/container. Give the
     * engine time to finish, then signal only this helper's remaining children. */
    gint64 started = g_get_monotonic_time();
    while (g_get_monotonic_time() - started < 3 * G_TIME_SPAN_SECOND) {
        while (g_main_context_pending(NULL)) g_main_context_iteration(NULL, FALSE);
        int result;
        do { result = waitpid(-1, NULL, WNOHANG); } while (result > 0);
        if (result < 0 && errno == ECHILD) return;
        if (g_get_monotonic_time() - started > G_TIME_SPAN_SECOND) {
            char *path = g_strdup_printf("/proc/self/task/%d/children", getpid());
            char *children = NULL;
            if (g_file_get_contents(path, &children, NULL, NULL)) {
                char **pids = g_strsplit(children, " ", -1);
                for (unsigned i = 0; pids[i]; ++i) {
                    pid_t pid = (pid_t)g_ascii_strtoll(pids[i], NULL, 10);
                    if (pid > 0 && waitpid(pid, NULL, WNOHANG) == 0)
                        kill(pid, g_get_monotonic_time() - started > 2 * G_TIME_SPAN_SECOND ? SIGKILL : SIGTERM);
                }
                g_strfreev(pids);
            }
            g_free(children); g_free(path);
        }
        g_usleep(10000);
    }
    while (waitpid(-1, NULL, WNOHANG) > 0) { }
}

static void frame_rendered(WPEView *view, WPEBuffer *buffer, gpointer data)
{
    (void)view;
    App *app = data;
    gint64 started = g_get_monotonic_time();
    if (!app->loaded || !app->capture_enabled || !bridge_connected(app->bridge) || started + 1000 < app->next_frame) return;
    if (!WPE_IS_BUFFER_SHM(buffer)
        || wpe_buffer_shm_get_format(WPE_BUFFER_SHM(buffer)) != WPE_PIXEL_FORMAT_ARGB8888) {
        status(app, "error", "CPU backend requires WPE ARGB8888 shared-memory buffers; GPU import is not implemented");
        return;
    }
    gint64 interval = (gint64)(G_TIME_SPAN_SECOND / app->fps);
    app->next_frame = MAX(app->next_frame + interval, started);
    GError *error = NULL;
    GBytes *bytes = wpe_buffer_import_to_pixels(buffer, &error);
    if (!bytes) {
        status(app, "error", error ? error->message : "WPE buffer cannot be imported as CPU pixels");
        g_clear_error(&error);
        return;
    }
    unsigned width = (unsigned)wpe_buffer_get_width(buffer), height = (unsigned)wpe_buffer_get_height(buffer);
    unsigned stride = WPE_IS_BUFFER_SHM(buffer) ? wpe_buffer_shm_get_stride(WPE_BUFFER_SHM(buffer)) : width * 4;
    gsize size;
    const unsigned char *pixels = g_bytes_get_data(bytes, &size);
    if (stride >= width * 4 && size >= (gsize)stride * height)
        bridge_frame(app->bridge, pixels, width, height, stride, g_get_real_time() / 1000);
    else status(app, "error", "Invalid WPE CPU buffer layout");
    if (!app->acceleration_reported) {
        status(app, "buffer", G_OBJECT_TYPE_NAME(buffer));
        app->acceleration_reported = TRUE;
    }
    /* import_to_pixels returns a borrowed GBytes owned by the WPE buffer. */
    app->frames++;
    app->export_us += g_get_monotonic_time() - started;
}

static void script_message(WebKitUserContentManager *manager, JSCValue *value, gpointer data)
{
    (void)manager;
    App *app = data;
    if (!jsc_value_is_string(value)) return;
    char *message = jsc_value_to_string(value);
    if (strlen(message) <= 16384) {
        JsonParser *parser = json_parser_new();
        if (json_parser_load_from_data(parser, message, -1, NULL)
            && JSON_NODE_HOLDS_OBJECT(json_parser_get_root(parser))) {
            JsonBuilder *builder = json_builder_new();
            json_builder_begin_object(builder);
            json_builder_set_member_name(builder, "type"); json_builder_add_string_value(builder, "message");
            json_builder_set_member_name(builder, "message"); json_builder_add_string_value(builder, message);
            json_builder_end_object(builder);
            send_object(app, builder);
        }
        g_object_unref(parser);
    }
    g_free(message);
}
static gboolean http_uri(const char *uri)
{
    GUri *parsed = g_uri_parse(uri, G_URI_FLAGS_NONE, NULL);
    if (!parsed) return FALSE;
    const char *scheme = g_uri_get_scheme(parsed);
    gboolean valid = scheme && (!g_ascii_strcasecmp(scheme, "http") || !g_ascii_strcasecmp(scheme, "https"))
        && g_uri_get_host(parsed) && !g_uri_get_userinfo(parsed) && strlen(uri) <= 2048;
    g_uri_unref(parsed);
    return valid;
}
static gboolean decide_policy(WebKitWebView *web, WebKitPolicyDecision *decision, WebKitPolicyDecisionType type, gpointer data)
{
    (void)web;
    App *app = data;
    if (type == WEBKIT_POLICY_DECISION_TYPE_NEW_WINDOW_ACTION) {
        webkit_policy_decision_ignore(decision); return TRUE;
    }
    if (type == WEBKIT_POLICY_DECISION_TYPE_RESPONSE) {
        WebKitResponsePolicyDecision *response = WEBKIT_RESPONSE_POLICY_DECISION(decision);
        const char *uri = webkit_uri_response_get_uri(webkit_response_policy_decision_get_response(response));
        if (webkit_response_policy_decision_is_main_frame_main_resource(response)
            && (!app->generic || app->locked_navigation)
            && (!app->expected_uri || strcmp(uri, app->expected_uri))) {
            webkit_policy_decision_ignore(decision);
            status(app, "navigation-blocked", "Top-level navigation rejected by native browser policy");
            return TRUE;
        }
        return FALSE;
    }
    if (type != WEBKIT_POLICY_DECISION_TYPE_NAVIGATION_ACTION) return FALSE;
    WebKitNavigationAction *action = webkit_navigation_policy_decision_get_navigation_action(WEBKIT_NAVIGATION_POLICY_DECISION(decision));
    const char *uri = webkit_uri_request_get_uri(webkit_navigation_action_get_request(action));
    gboolean allowed = http_uri(uri) || !strcmp(uri, "about:blank");
    /* NavigationAction.frame_name is a target name, not a reliable main-frame
     * identifier. Use ResponsePolicyDecision for the provider top-level lock.
     * IPC Generic Web conservatively locks ALL document navigations to the
     * approved initial URL until asynchronous host approval is implemented. */
    if (app->locked_navigation)
        allowed = app->expected_uri && !strcmp(uri, app->expected_uri);
    if (!allowed) {
        webkit_policy_decision_ignore(decision);
        status(app, "navigation-blocked", "Navigation rejected by native browser policy");
        return TRUE;
    }
    return FALSE;
}
static gboolean deny_permission(WebKitWebView *web, WebKitPermissionRequest *request, gpointer data)
{
    (void)web; (void)data;
    webkit_permission_request_deny(request);
    return TRUE;
}
static WebKitWebView *block_create(WebKitWebView *web, WebKitNavigationAction *action, gpointer data)
{
    (void)web; (void)action; (void)data;
    return NULL;
}
static void download_started(WebKitNetworkSession *session, WebKitDownload *download, gpointer data)
{
    (void)session; (void)data;
    webkit_download_cancel(download);
}
static gboolean load_failed(WebKitWebView *web, WebKitLoadEvent event, const char *uri, GError *error, gpointer data)
{
    (void)web; (void)event; (void)uri;
    status(data, "error", error->message);
    return TRUE;
}
static void load_changed(WebKitWebView *web, WebKitLoadEvent event, gpointer data)
{
    (void)web;
    if (event == WEBKIT_LOAD_FINISHED) status(data, "loaded", "Top-level document load finished");
}
static void web_crashed(WebKitWebView *web, WebKitWebProcessTerminationReason reason, gpointer data)
{
    (void)web; (void)reason;
    status(data, "error", "Native WebKit web process terminated; restart the screen to retry");
    g_timeout_add(100, quit, data);
}
typedef struct { App *app; gint64 id; } Evaluation;
static void evaluated(GObject *object, GAsyncResult *result, gpointer data)
{
    Evaluation *evaluation = data;
    GError *error = NULL;
    JSCValue *value = webkit_web_view_evaluate_javascript_finish(WEBKIT_WEB_VIEW(object), result, &error);
    if (evaluation->app->stopping) { g_clear_error(&error); g_clear_object(&value); g_free(evaluation); return; }
    if (evaluation->id) {
        char *text = value ? jsc_value_to_json(value, 0) : NULL;
        JsonBuilder *builder = json_builder_new();
        json_builder_begin_object(builder);
        json_builder_set_member_name(builder, "type"); json_builder_add_string_value(builder, "evaluation");
        json_builder_set_member_name(builder, "id"); json_builder_add_int_value(builder, evaluation->id);
        json_builder_set_member_name(builder, "result"); json_builder_add_string_value(builder, text ? text : "null");
        if (error) { json_builder_set_member_name(builder, "error"); json_builder_add_string_value(builder, error->message); }
        json_builder_end_object(builder);
        send_object(evaluation->app, builder);
        g_free(text);
    } else if (error) status(evaluation->app, "error", error->message);
    g_clear_error(&error); g_clear_object(&value); g_free(evaluation);
}
static void evaluate(App *app, const char *script, gint64 id)
{
    Evaluation *evaluation = g_new(Evaluation, 1);
    *evaluation = (Evaluation) { app, id };
    webkit_web_view_evaluate_javascript(app->web, script, -1, NULL, NULL, NULL, evaluated, evaluation);
}
static gboolean has_decoder(const char *name)
{
    GstElementFactory *factory = gst_element_factory_find(name);
    if (!factory) return FALSE;
    gst_object_unref(factory);
    return TRUE;
}
static void command(JsonObject *object, void *data)
{
    App *app = data;
    if (app->stopping) return;
    const char *type = member_string(object, "type");
    if (!strcmp(type, "ping")) { bridge_json(app->bridge, "{\"type\":\"pong\"}"); return; }
    if (!strcmp(type, "probe")) {
        /* Dispatched on the main loop, after WebKit initialized successfully. */
        char json[256];
        g_snprintf(json, sizeof json, "{\"type\":\"probe\",\"protocol\":1,\"major\":%u,\"minor\":%u,\"micro\":%u,\"h264Software\":%s,\"aacSoftware\":%s}",
            webkit_get_major_version(), webkit_get_minor_version(), webkit_get_micro_version(),
            has_decoder("avdec_h264") || has_decoder("openh264dec") ? "true" : "false",
            has_decoder("avdec_aac") || has_decoder("faad") ? "true" : "false");
        bridge_json(app->bridge, json);
        return;
    }
    if (!strcmp(type, "shutdown")) { quit(app); return; }
    if (!strcmp(type, "capture")) {
        app->capture_enabled = json_object_get_boolean_member_with_default(object, "enabled", FALSE);
        return;
    }
    if (!strcmp(type, "ack")) {
        bridge_ack(app->bridge, (unsigned)json_object_get_int_member_with_default(object, "slot", -1),
                   (uint64_t)json_object_get_int_member_with_default(object, "sequence", -1));
        return;
    }
    if (!strcmp(type, "fps")) {
        app->fps = CLAMP(json_object_get_double_member_with_default(object, "fps", 30), 1, 120);
        return;
    }
    if (!strcmp(type, "load") && !app->loaded) {
        const char *uri = member_string(object, "uri");
        if (!http_uri(uri)) { status(app, "error", "Invalid initial browser URL"); return; }
        app->width = CLAMP(json_object_get_int_member_with_default(object, "width", 1280), 320, CC_MAX_WIDTH);
        app->height = CLAMP(json_object_get_int_member_with_default(object, "height", 720), 180, CC_MAX_HEIGHT);
        app->fps = CLAMP(json_object_get_double_member_with_default(object, "fps", 60), 1, 120);
        app->generic = json_object_get_boolean_member_with_default(object, "generic", FALSE);
        app->locked_navigation = json_object_get_boolean_member_with_default(object, "lockNavigation", FALSE);
        app->expected_uri = g_strdup(uri);
        const char *script = member_string(object, "script");
        if (*script) {
            WebKitUserScript *user_script = webkit_user_script_new(script, WEBKIT_USER_CONTENT_INJECT_TOP_FRAME,
                WEBKIT_USER_SCRIPT_INJECT_AT_DOCUMENT_END, NULL, NULL);
            webkit_user_content_manager_add_script(app->manager, user_script);
            webkit_user_script_unref(user_script);
        }
        WPEView *view = webkit_web_view_get_wpe_view(app->web);
        WPEToplevel *toplevel = wpe_view_get_toplevel(view);
        wpe_toplevel_resize(toplevel, app->width, app->height);
        wpe_toplevel_state_changed(toplevel, WPE_TOPLEVEL_STATE_ACTIVE);
        wpe_view_set_visible(view, TRUE);
        wpe_view_map(view);
        wpe_view_focus_in(view);
        app->loaded = TRUE;
        if (app->generic) webkit_web_view_load_uri(app->web, uri);
        else webkit_web_view_load_html(app->web, member_string(object, "html"), uri);
        char *version = g_strdup_printf("WPE WebKit %u.%u.%u; uncompressed CPU-frame export; ephemeral profile",
            webkit_get_major_version(), webkit_get_minor_version(), webkit_get_micro_version());
        status(app, "engine", version);
        g_free(version);
        return;
    }
    if (!app->loaded) return;
    if (!strcmp(type, "command")) {
        const char *message = member_string(object, "message");
        JsonParser *parser = json_parser_new();
        if (strlen(message) <= 16384 && json_parser_load_from_data(parser, message, -1, NULL)
            && JSON_NODE_HOLDS_OBJECT(json_parser_get_root(parser))) {
            char *script = g_strdup_printf("window.__crystalcastNativeDispatch(%s)", message);
            evaluate(app, script, 0);
            g_free(script);
        }
        g_object_unref(parser);
    } else if (!strcmp(type, "evaluate"))
        evaluate(app, member_string(object, "script"), json_object_get_int_member_with_default(object, "id", 0));
}
static gboolean metrics(gpointer data)
{
    App *app = data;
    char json[320];
    g_snprintf(json, sizeof json, "{\"type\":\"metrics\",\"rendered\":%" G_GINT64_FORMAT ",\"published\":%" G_GUINT64_FORMAT ",\"dropped\":%" G_GUINT64_FORMAT ",\"exportUs\":%" G_GINT64_FORMAT "}",
               app->frames, bridge_frame_count(app->bridge), bridge_drop_count(app->bridge), app->export_us);
    bridge_json(app->bridge, json);
    return G_SOURCE_CONTINUE;
}

int main(int argc, char **argv)
{
    gchar *session = NULL, *runtime = NULL;
    gboolean diagnose = FALSE;
    GOptionEntry entries[] = {
        { "session", 0, 0, G_OPTION_ARG_STRING, &session, "Per-screen 32-hex session identifier", "ID" },
        { "runtime-root", 0, 0, G_OPTION_ARG_STRING, &runtime, "Private runtime root (default XDG_RUNTIME_DIR)", "PATH" },
        { "diagnose", 0, 0, G_OPTION_ARG_NONE, &diagnose, "Report runtime/codec dependencies", NULL },
        { NULL, 0, 0, 0, NULL, NULL, NULL }
    };
    GError *error = NULL;
    GOptionContext *options = g_option_context_new("- CrystalCast experimental native Linux browser");
    g_option_context_add_main_entries(options, entries, NULL);
    if (!g_option_context_parse(options, &argc, &argv, &error)) { g_printerr("%s\n", error->message); return 2; }
    gst_init(NULL, NULL);
    if (diagnose) {
        printf("WPE WebKit %u.%u.%u; GStreamer %s\n", webkit_get_major_version(), webkit_get_minor_version(), webkit_get_micro_version(), gst_version_string());
        const char *elements[] = { "qtdemux", "h264parse", "avdec_h264", "avdec_aac", "faad", "vp9dec", "opusdec", "av1dec", "vah264dec", "vavp9dec", "nvh264dec", NULL };
        for (unsigned i = 0; elements[i]; ++i) {
            GstElementFactory *factory = gst_element_factory_find(elements[i]);
            printf("%s: %s\n", elements[i], factory ? "available (not proof of active decoding)" : "missing");
            if (factory) gst_object_unref(factory);
        }
        return 0;
    }
    if (getuid() == 0) { g_printerr("Run the browser as your desktop user, not root.\n"); return 2; }
#if G_BYTE_ORDER != G_LITTLE_ENDIAN
    g_printerr("CPU frame export currently requires a little-endian Linux host.\n"); return 2;
#endif
    umask(0077);
    prctl(PR_SET_PDEATHSIG, SIGTERM);
    prctl(PR_SET_CHILD_SUBREAPER, 1);
    if (!runtime) runtime = g_strdup(g_get_user_runtime_dir());
    g_setenv("WPE_PLATFORM", "headless", TRUE);
    /* Deliberate reliable baseline. Hardware decode/export needs independent
     * validation; compositor-buffer callbacks still deliver real video frames. */
    g_setenv("WEBKIT_DISABLE_COMPOSITING_MODE", "1", FALSE);
    // The rendered output is consumed by the game, not a Linux desktop window.
    // WebKit otherwise suspends muted video it classifies as outside a viewport.
    g_setenv("WEBKIT_GST_ALLOW_PLAYBACK_OF_INVISIBLE_VIDEOS", "1", TRUE);
    App app = { .loop = g_main_loop_new(NULL, FALSE), .fps = 60, .capture_enabled = TRUE };
    app.bridge = bridge_new(session, runtime, command, peer_disconnected, &app, &error);
    if (!app.bridge) { g_printerr("%s\n", error->message); return 2; }
    app.manager = webkit_user_content_manager_new();
    webkit_user_content_manager_register_script_message_handler(app.manager, "crystalcast", NULL);
    g_signal_connect(app.manager, "script-message-received::crystalcast", G_CALLBACK(script_message), &app);
    WebKitUserScript *shim = webkit_user_script_new(host_bridge, WEBKIT_USER_CONTENT_INJECT_TOP_FRAME,
        WEBKIT_USER_SCRIPT_INJECT_AT_DOCUMENT_START, NULL, NULL);
    webkit_user_content_manager_add_script(app.manager, shim);
    webkit_user_script_unref(shim);
    WebKitSettings *settings = webkit_settings_new();
    webkit_settings_set_media_playback_requires_user_gesture(settings, FALSE);
    webkit_settings_set_enable_mediasource(settings, TRUE);
    WebKitNetworkSession *network = webkit_network_session_new_ephemeral();
    g_signal_connect(network, "download-started", G_CALLBACK(download_started), &app);
    app.web = g_object_new(WEBKIT_TYPE_WEB_VIEW, "display", wpe_display_get_default(),
        "network-session", network, "settings", settings, "user-content-manager", app.manager, NULL);
    g_signal_connect(webkit_web_view_get_wpe_view(app.web), "buffer-rendered", G_CALLBACK(frame_rendered), &app);
    g_signal_connect(app.web, "decide-policy", G_CALLBACK(decide_policy), &app);
    g_signal_connect(app.web, "permission-request", G_CALLBACK(deny_permission), &app);
    g_signal_connect(app.web, "create", G_CALLBACK(block_create), &app);
    g_signal_connect(app.web, "load-failed", G_CALLBACK(load_failed), &app);
    g_signal_connect(app.web, "load-changed", G_CALLBACK(load_changed), &app);
    g_signal_connect(app.web, "web-process-terminated", G_CALLBACK(web_crashed), &app);
    guint metric_source = g_timeout_add_seconds(1, metrics, &app);
    guint term_source = g_unix_signal_add(SIGTERM, signal_quit, &app);
    guint int_source = g_unix_signal_add(SIGINT, signal_quit, &app);
    g_main_loop_run(app.loop);
    g_source_remove(metric_source); g_source_remove(term_source); g_source_remove(int_source);
    app.stopping = TRUE;
    webkit_web_view_stop_loading(app.web);
    g_signal_handlers_disconnect_by_data(webkit_web_view_get_wpe_view(app.web), &app);
    g_signal_handlers_disconnect_by_data(app.web, &app);
    g_signal_handlers_disconnect_by_data(app.manager, &app);
    bridge_free(app.bridge);
    g_object_unref(app.web);
    g_object_unref(network); g_object_unref(settings); g_object_unref(app.manager);
    reap_children();
    g_main_loop_unref(app.loop);
    g_free(app.expected_uri); g_free(session); g_free(runtime);
    g_option_context_free(options);
    return 0;
}
