/* Experiment: capture a Wine window's pixels from Linux with the X11 Composite
 * extension, optionally while another window covers it completely.
 * Usage: x11capture <window id> <seconds> [cover]
 * Build: cc -O2 -o x11capture x11capture.c -lX11 -lXcomposite */
#include <X11/Xlib.h>
#include <X11/Xutil.h>
#include <X11/extensions/Xcomposite.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

static double now(void)
{
    struct timespec t;
    clock_gettime(CLOCK_MONOTONIC, &t);
    return t.tv_sec + t.tv_nsec / 1e9;
}

static uint64_t hash_image(XImage *image)
{
    uint64_t h = 14695981039346656037ULL;
    size_t bytes = (size_t) image->bytes_per_line * image->height;
    for (size_t i = 0; i < bytes; i += 4099)
        h = (h ^ (unsigned char) image->data[i]) * 1099511628211ULL;
    return h;
}

/* Measures how many distinct frames one window yields in a second, read directly and
 * through its Composite pixmap. */
static int ignore_errors(Display *display, XErrorEvent *event)
{
    (void) display; (void) event;
    return 0;
}

static void survey_window(Display *display, Window window, int depth)
{
    XWindowAttributes a;
    if (!XGetWindowAttributes(display, window, &a)) return;
    if (a.width >= 64 && a.height >= 64 && a.map_state == IsViewable) {
        int distinct_direct = 0, distinct_pixmap = 0;
        uint64_t last_direct = 0, last_pixmap = 0;
        double start = now();
        for (int i = 0; now() - start < 1.0; ++i) {
            XImage *direct = XGetImage(display, window, 0, 0, a.width, a.height, AllPlanes, ZPixmap);
            if (direct) {
                uint64_t h = hash_image(direct);
                if (i == 0 || h != last_direct) ++distinct_direct;
                last_direct = h;
                XDestroyImage(direct);
            }
            Pixmap pixmap = depth > 0 ? XCompositeNameWindowPixmap(display, window) : 0;
            XImage *named = pixmap ? XGetImage(display, pixmap, 0, 0, a.width, a.height, AllPlanes, ZPixmap) : NULL;
            if (pixmap) XFreePixmap(display, pixmap);
            if (named) {
                uint64_t h = hash_image(named);
                if (i == 0 || h != last_pixmap) ++distinct_pixmap;
                last_pixmap = h;
                XDestroyImage(named);
            }
        }
        printf("%*swindow 0x%lx %dx%d at %d,%d: direct %d distinct/s, composite pixmap %d distinct/s\n",
            depth * 2, "", (unsigned long) window, a.width, a.height, a.x, a.y, distinct_direct, distinct_pixmap);
    }
    Window root, parent, *children = NULL;
    unsigned int count = 0;
    if (XQueryTree(display, window, &root, &parent, &children, &count)) {
        for (unsigned int i = 0; i < count; ++i)
            survey_window(display, children[i], depth + 1);
        if (children) XFree(children);
    }
}

int main(int argc, char **argv)
{
    if (argc > 1 && !strcmp(argv[1], "survey")) {
        /* Usage: x11capture survey  -- every viewable window on the screen. */
        Display *display = XOpenDisplay(NULL);
        if (!display) return 1;
        XSetErrorHandler(ignore_errors);
        XCompositeRedirectSubwindows(display, DefaultRootWindow(display), CompositeRedirectAutomatic);
        XSync(display, False);
        survey_window(display, DefaultRootWindow(display), 0);
        XCloseDisplay(display);
        return 0;
    }
    if (argc < 3) {
        fprintf(stderr, "usage: %s <window id> <seconds> [cover]\n", argv[0]);
        return 2;
    }
    Window target = (Window) strtoul(argv[1], NULL, 0);
    double seconds = atof(argv[2]);
    int cover = argc > 3 && !strcmp(argv[3], "cover");

    Display *display = XOpenDisplay(NULL);
    if (!display) { fprintf(stderr, "cannot open display\n"); return 1; }
    int event_base, error_base;
    if (!XCompositeQueryExtension(display, &event_base, &error_base)) {
        fprintf(stderr, "Composite extension unavailable\n");
        return 1;
    }

    XWindowAttributes attributes;
    if (!XGetWindowAttributes(display, target, &attributes)) { fprintf(stderr, "bad window\n"); return 1; }
    /* Keep the window's contents in an off-screen pixmap, so covering it does not lose them. */
    XCompositeRedirectWindow(display, target, CompositeRedirectAutomatic);
    XSync(display, False);

    if (cover) {
        /* Stand-in for the game: an opaque window over the whole screen. */
        Window root = DefaultRootWindow(display);
        int screen = DefaultScreen(display);
        Window blocker = XCreateSimpleWindow(display, root, 0, 0, DisplayWidth(display, screen),
            DisplayHeight(display, screen), 0, 0, 0x00ff00);
        XSetWindowAttributes override = { .override_redirect = True };
        XChangeWindowAttributes(display, blocker, CWOverrideRedirect, &override);
        XMapRaised(display, blocker);
        XSync(display, False);
    }

    int captures = 0, distinct = 0, black = 0;
    uint64_t last = 0;
    double total = 0, start = now();
    while (now() - start < seconds) {
        double t0 = now();
        Pixmap pixmap = XCompositeNameWindowPixmap(display, target);
        XImage *image = XGetImage(display, pixmap, 0, 0, attributes.width, attributes.height, AllPlanes, ZPixmap);
        double t1 = now();
        XFreePixmap(display, pixmap);
        if (!image) { fprintf(stderr, "XGetImage failed\n"); break; }
        uint64_t h = hash_image(image);
        unsigned long center = XGetPixel(image, attributes.width / 2, attributes.height / 2);
        if (center == 0) ++black;
        if (captures == 0 || h != last) ++distinct;
        last = h;
        ++captures;
        total += t1 - t0;
        XDestroyImage(image);
    }

    printf("{\"window\":\"0x%lx\",\"size\":\"%dx%d\",\"covered\":%s,\"captures\":%d,\"distinct_frames\":%d,"
           "\"distinct_fps\":%.1f,\"mean_ms\":%.2f,\"black_center_captures\":%d}\n",
        (unsigned long) target, attributes.width, attributes.height, cover ? "true" : "false",
        captures, distinct, distinct / seconds, captures ? total * 1000 / captures : 0, black);
    XCloseDisplay(display);
    return 0;
}
