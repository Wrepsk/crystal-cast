# Third-party notices

CrystalCast depends on third-party libraries whose own licenses continue to apply.

## CrystalRenderer derivation from Pictomancy

CrystalRenderer includes a focused derivative of the screen-rendering code from [sourpuh/ffxiv_pictomancy](https://github.com/sourpuh/ffxiv_pictomancy) and the CrystalCast-specific [Wrepsk fork](https://github.com/Wrepsk/ffxiv_pictomancy), extracted at commit `ed205c725fe1d17124480823eac7affaccd669ee`. Original authorship and inline credits are retained. Ported files are marked with attribution; the upstream AGPL license text is included at `CrystalRenderer/LICENSE.Pictomancy`.

Pictomancy is distributed under `AGPL-3.0-or-later`. CrystalRenderer remains under that license. The original Pictomancy submodule has been removed. The source links and extraction commit above record its provenance; `Pictomancy.dll` is not included in releases.

## Packaged runtime libraries

The release archive includes the runtime assemblies selected by the locked dependency graph, including Microsoft WebView2, ImageSharp, KamiToolKit, and SharpDX components. Package versions and integrity hashes are recorded in the project lock files, including `CrystalCast/packages.lock.json` and `CrystalRenderer/packages.lock.json`; their respective upstream licenses apply.

ImageSharp remains at its existing version during the renderer migration. Its direct/transitive uses, current audit findings, and separate upgrade/removal follow-up are documented in `docs/DEPENDENCY_AUDIT.md`.

## Optional native Linux browser runtime

The optional Linux Browser package is distributed separately from the Windows plugin. It contains WPE WebKit, GStreamer and multimedia libraries, sandbox utilities, fonts and their runtime dependencies. Each package carries a component inventory with exact binary/source versions, per-component copyright notices, common license texts and WPE third-party notices. Their licenses continue to apply; this notice does not replace them.

Matching distribution source packages, the pinned WPE source, CrystalCast helper sources and relocation/build recipes must accompany each published runtime release. See [runtime distribution and source obligations](docs/LINUX_BROWSER_DISTRIBUTION.md). No portable runtime release is enabled in the plugin catalog until its artifacts and sources have been verified.

The native bootstrap statically links C-runtime and compiler support code. Its corresponding glibc/GCC sources, license notices, application object and relinking instructions accompany the optional runtime's source release; these are additional to the browser's shared-library sources.
