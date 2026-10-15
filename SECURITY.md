# Security policy

## Supported versions

Security fixes are applied to the current `master` branch and the latest tagged release. Older releases may not receive backports.

## Reporting a vulnerability

Do not open a public issue for a vulnerability that could expose users, browser data, local files, or another plugin's state. Use GitHub's private vulnerability reporting for this repository when available. If it is unavailable, contact the maintainer privately through the repository owner's GitHub profile and include a safe way to continue the discussion.

Include the affected version or commit, impact, reproduction conditions, and any suggested mitigation. Avoid including account data, browser profiles, credentials, or destructive proof-of-concept steps.

## Security boundaries

CrystalCast renders browser content locally and exposes state-only Dalamud IPC. It must not:

- execute downloaded programs or silently modify Wine prefixes;
- expose browser credentials, cookies, local files, or profile data through IPC;
- accept unbounded or unauthenticated browser telemetry;
- permit provider pages to open popups, downloads, external schemes, or sensitive browser permissions;
- treat owner IDs or source locks as authentication boundaries;
- automate gameplay or communicate with FFXIV servers.

Generic Web intentionally loads a user-supplied HTTP/HTTPS page and therefore has a broader trust boundary. Reports that demonstrate an escape beyond the documented page-level tracking and navigation behavior are in scope.

The experimental native Linux helper keeps WebKit's sandbox enabled, runs as the desktop user, uses ephemeral per-screen profiles, and authenticates its loopback connection with a random token stored only in a private runtime file. Shared frame slots require sequence-matched acknowledgement before reuse. Native IPC credentials, endpoint files and frame data must not be included in diagnostics or commits. See [native IPC/lifecycle boundaries](NativeLinuxBrowser/README.md#ipc-and-lifecycle), including the conservative navigation restriction for IPC-approved Generic Web pages. System WebKit and GStreamer security updates remain the Linux user's/package manager's responsibility.
