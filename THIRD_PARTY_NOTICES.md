# Third-party notices

CrystalCast depends on third-party libraries whose own licenses continue to apply.

## CrystalRenderer derivation from Pictomancy

CrystalRenderer includes a focused derivative of the screen-rendering code from [sourpuh/ffxiv_pictomancy](https://github.com/sourpuh/ffxiv_pictomancy) and the CrystalCast-specific [Wrepsk fork](https://github.com/Wrepsk/ffxiv_pictomancy), extracted at commit `ed205c725fe1d17124480823eac7affaccd669ee`. Original authorship and inline credits are retained. Ported files are marked with attribution; the upstream AGPL license text is included at `CrystalRenderer/LICENSE.Pictomancy`.

Pictomancy is distributed under `AGPL-3.0-or-later`. CrystalRenderer remains under that license. The original Pictomancy submodule has been removed. The source links and extraction commit above record its provenance; `Pictomancy.dll` is not included in releases.

## Packaged runtime libraries

The release archive includes the runtime assemblies selected by the locked dependency graph, including Microsoft WebView2, ImageSharp, KamiToolKit, and SharpDX components. Package versions and integrity hashes are recorded in the project lock files, including `CrystalCast/packages.lock.json` and `CrystalRenderer/packages.lock.json`; their respective upstream licenses apply.

ImageSharp remains at its existing version during the renderer migration. Its direct/transitive uses, current audit findings, and separate upgrade/removal follow-up are documented in `docs/DEPENDENCY_AUDIT.md`.
