/* Native bootstrap for the optional runtime. Built statically so Proton's
 * LD_LIBRARY_PATH/LD_PRELOAD cannot affect it before main() clears them. */
#define _GNU_SOURCE
#include <errno.h>
#include <limits.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <sys/wait.h>
#include <signal.h>
#include <time.h>
#include <unistd.h>

static char root[PATH_MAX];
static int sandbox_ready(void)
{
    if (access("/.flatpak-info", F_OK) == 0 || (getenv("SNAP") && getenv("SNAP_NAME") && getenv("SNAP_REVISION"))) return 0;
    pid_t child = fork();
    if (child < 0) return 0;
    if (!child) {
        const char *bwrap = getenv("CRYSTALCAST_RUNTIME_BWRAP");
        execl(bwrap, bwrap, "--ro-bind", "/", "/", "--proc", "/proc", "--dev", "/dev",
            "--unshare-all", "/usr/bin/true", (char *)NULL);
        _exit(127);
    }
    int status = 0;
    // Refuse unsupported sandboxing before the helper can publish IPC files.
    for (unsigned attempt = 0; attempt < 500; attempt++) {
        pid_t result = waitpid(child, &status, WNOHANG);
        if (result == child) return WIFEXITED(status) && WEXITSTATUS(status) == 0;
        if (result < 0 && errno != EINTR) return 0;
        struct timespec pause = { .tv_nsec = 10000000 };
        nanosleep(&pause, NULL);
    }
    kill(child, SIGKILL); waitpid(child, &status, 0);
    return 0;
}
static void setting(const char *name, const char *suffix)
{
    char path[PATH_MAX];
    if (snprintf(path, sizeof path, "%s/%s", root, suffix) >= (int)sizeof path || setenv(name, path, 1)) {
        fputs("CrystalCast runtime path is too long or environment setup failed.\n", stderr);
        exit(2);
    }
}

int main(int argc, char **argv)
{
    ssize_t count = readlink("/proc/self/exe", root, sizeof root - 1);
    if (count <= 0 || count >= (ssize_t)sizeof root - 1) return 2;
    root[count] = 0;
    char *slash = strrchr(root, '/');
    if (!slash) return 2;
    *slash = 0;
    slash = strrchr(root, '/');
    if (!slash) return 2;
    *slash = 0;
    struct stat status;
    if (lstat(root, &status) || !S_ISDIR(status.st_mode) || status.st_uid != getuid() || (status.st_mode & 0022)) {
        fputs("CrystalCast runtime must be owned by the desktop user and not writable by other users.\n", stderr);
        return 2;
    }
    const char *clear[] = { "LD_PRELOAD", "LD_AUDIT", "LD_LIBRARY_PATH", "GIO_EXTRA_MODULES",
        "GST_PLUGIN_PATH", "GST_PLUGIN_PATH_1_0", "GST_PLUGIN_SYSTEM_PATH", "GST_PLUGIN_SYSTEM_PATH_1_0",
        "GST_PLUGIN_SCANNER", "GST_PLUGIN_SCANNER_1_0", "WPE_PLATFORMS_PATH", "WEBKIT_EXEC_PATH", "WEBKIT_INJECTED_BUNDLE_PATH",
        "WEBKIT_DISABLE_SANDBOX_THIS_IS_DANGEROUS", NULL };
    for (unsigned i = 0; clear[i]; i++) unsetenv(clear[i]);
    setenv("CRYSTALCAST_RUNTIME_ROOT", root, 1);
    setting("LD_LIBRARY_PATH", "lib");
    setting("CRYSTALCAST_RUNTIME_EXEC_DIR", "libexec/wpe-webkit-2.0");
    // Prefer a distribution-managed bubblewrap when present: Ubuntu may attach
    // a user-namespace AppArmor profile to that exact system executable path.
    if (access("/usr/bin/bwrap", X_OK) == 0) setenv("CRYSTALCAST_RUNTIME_BWRAP", "/usr/bin/bwrap", 1);
    else setting("CRYSTALCAST_RUNTIME_BWRAP", "libexec/bwrap");
    setting("CRYSTALCAST_RUNTIME_DBUS_PROXY", "libexec/xdg-dbus-proxy");
    setting("WEBKIT_INJECTED_BUNDLE_PATH", "lib/wpe-webkit-2.0/injected-bundle");
    setting("GST_PLUGIN_SYSTEM_PATH_1_0", "lib/gstreamer-1.0");
    setting("GST_PLUGIN_SCANNER_1_0", "libexec/gstreamer-1.0/gst-plugin-scanner");
    setting("GIO_MODULE_DIR", "lib/gio/modules");
    setting("GSETTINGS_SCHEMA_DIR", "share/glib-2.0/schemas");
    setting("FONTCONFIG_FILE", "etc/fonts/fonts.conf");
    char data[PATH_MAX * 2];
    snprintf(data, sizeof data, "%s/share:/usr/local/share:/usr/share", root);
    setenv("XDG_DATA_DIRS", data, 1);
    if (!getenv("__EGL_VENDOR_LIBRARY_DIRS") && !getenv("__EGL_VENDOR_LIBRARY_FILENAMES")) {
        snprintf(data, sizeof data, "/etc/glvnd/egl_vendor.d:/usr/share/glvnd/egl_vendor.d:%s/share/glvnd/egl_vendor.d", root);
        setenv("__EGL_VENDOR_LIBRARY_DIRS", data, 1);
    }
    if (!getenv("LIBGL_DRIVERS_PATH")) {
        snprintf(data, sizeof data, "%s/lib/dri:/usr/lib/x86_64-linux-gnu/dri:/usr/lib64/dri:/usr/lib/dri", root);
        setenv("LIBGL_DRIVERS_PATH", data, 1);
    }
    const char *cache = getenv("XDG_CACHE_HOME");
    const char *home = getenv("HOME");
    char registry[PATH_MAX];
    if (cache && *cache == '/') snprintf(registry, sizeof registry, "%s/crystalcast/gstreamer-registry.bin", cache);
    else if (home && *home == '/') snprintf(registry, sizeof registry, "%s/.cache/crystalcast/gstreamer-registry.bin", home);
    else { fputs("CrystalCast runtime requires a desktop HOME or XDG_CACHE_HOME.\n", stderr); return 2; }
    setenv("GST_REGISTRY_1_0", registry, 1);
    int diagnose = 0;
    for (int i = 1; i < argc; i++) diagnose |= !strcmp(argv[i], "--diagnose");
    if (!diagnose && (getuid() == 0 || !sandbox_ready())) {
        fputs("CrystalCast Linux Browser cannot start its sandbox as this user/in this launcher. Sandboxing will not be disabled. See Advanced diagnostics.\n", stderr);
        return 2;
    }
    char executable[PATH_MAX];
    if (snprintf(executable, sizeof executable, "%s/libexec/crystalcast-browser-helper", root) >= (int)sizeof executable) return 2;
    argv[0] = executable;
    (void)argc;
    execv(executable, argv);
    fprintf(stderr, "CrystalCast browser launch failed: %s\n", strerror(errno));
    return 2;
}
