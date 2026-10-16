#!/usr/bin/env python3
"""Narrow relocation changes for the pinned WPE release, not developer mode.

Only the verified runtime launcher sets these paths. The runtime tree is mounted
read-only inside WebKit's existing sandbox; no sandbox protection is disabled.
Keep this script and the upstream source in the corresponding-source release.
"""
from pathlib import Path
import sys

source = Path(sys.argv[1]) / "Source/WebKit"


def edit(relative, old, new, count=1):
    path = source / relative
    text = path.read_text()
    if text.count(old) != count:
        raise RuntimeError(f"Unexpected upstream content in {relative}; relocation patch requires review")
    path.write_text(text.replace(old, new))


process = "Shared/glib/ProcessExecutablePathGLib.cpp"
edit(process, "static String findWebKitProcess(const ASCIILiteral processName)\n{",
     """static String findWebKitProcess(const ASCIILiteral processName)
{
    if (const char* directory = g_getenv("CRYSTALCAST_RUNTIME_EXEC_DIR")) {
        if (g_path_is_absolute(directory)) {
            String path = FileSystem::pathByAppendingComponent(FileSystem::stringFromFileSystemRepresentation(directory), processName);
            if (FileSystem::fileExists(path))
                return path;
        }
    }""")

function = """
// CrystalCast's portable runtime supplies absolute paths to its own tools.
static const char* crystalCastRuntimeExecutable(const char* variable, const char* fallback)
{
    const char* path = g_getenv(variable);
    return path && g_path_is_absolute(path) ? path : fallback;
}

"""
for file, replacements in (
    ("UIProcess/Launcher/glib/BubblewrapLauncher.cpp", [
        ("BWRAP_EXECUTABLE", 'crystalCastRuntimeExecutable("CRYSTALCAST_RUNTIME_BWRAP", BWRAP_EXECUTABLE)'),
        ("DBUS_PROXY_EXECUTABLE", 'crystalCastRuntimeExecutable("CRYSTALCAST_RUNTIME_DBUS_PROXY", DBUS_PROXY_EXECUTABLE)'),
    ]),
    ("UIProcess/Launcher/glib/XDGDBusProxy.cpp", [
        ("DBUS_PROXY_EXECUTABLE", 'crystalCastRuntimeExecutable("CRYSTALCAST_RUNTIME_DBUS_PROXY", DBUS_PROXY_EXECUTABLE)'),
    ]),
):
    # WebKit's unified build can include both .cpp files in one translation
    # unit, so their file-local helper names must also be distinct.
    helper = "crystalCastRuntimeProxyExecutable" if file.endswith("XDGDBusProxy.cpp") else "crystalCastRuntimeExecutable"
    for old, new in replacements:
        edit(file, old, new.replace("crystalCastRuntimeExecutable", helper), count=2 if file.endswith("BubblewrapLauncher.cpp") and old == "DBUS_PROXY_EXECUTABLE" else 1)
    edit(file, "namespace WebKit {", "namespace WebKit {\n" + function.replace("crystalCastRuntimeExecutable", helper))

edit("UIProcess/Launcher/glib/BubblewrapLauncher.cpp", "    if (enableDebugPermissions()) {", """    // Expose only the immutable browser runtime, never the user's home tree.
    if (const char* root = g_getenv("CRYSTALCAST_RUNTIME_ROOT")) {
        if (g_path_is_absolute(root) && g_file_test(root, G_FILE_TEST_IS_DIR))
            sandboxArgs.appendList<CString>({ "--ro-bind", root, root });
    }

    if (enableDebugPermissions()) {""")

edit("../WTF/wtf/glib/Sandbox.cpp", "            BWRAP_EXECUTABLE,", """            g_getenv("CRYSTALCAST_RUNTIME_BWRAP") ? g_getenv("CRYSTALCAST_RUNTIME_BWRAP") : BWRAP_EXECUTABLE,""")
edit("../WTF/wtf/glib/Sandbox.cpp", "        if (!supportedContainer)\n            WTFLogAlways", "        if (!supportedContainer && !g_getenv(\"CRYSTALCAST_RUNTIME_ROOT\"))\n            WTFLogAlways")
edit("../WTF/wtf/glib/Sandbox.cpp", "    return !isInsideFlatpak() && !isInsideSnap() && !isInsideUnsupportedContainer();", """    if (g_getenv("CRYSTALCAST_RUNTIME_ROOT")) {
        if (isInsideFlatpak() || isInsideSnap() || isInsideUnsupportedContainer())
            g_error("CrystalCast Linux Browser cannot initialize its sandbox in this environment. Sandboxing will not be disabled.");
        return true;
    }
    return !isInsideFlatpak() && !isInsideSnap() && !isInsideUnsupportedContainer();""")
