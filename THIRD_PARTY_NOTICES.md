# Third-party notices

CrystalCast depends on third-party libraries whose own licenses continue to apply.

## CrystalRenderer derivation from Pictomancy

CrystalRenderer is maintained in [Wrepsk/crystal-renderer](https://github.com/Wrepsk/crystal-renderer) and pinned here as a Git submodule. It includes a focused derivative of the screen-rendering code from [sourpuh/ffxiv_pictomancy](https://github.com/sourpuh/ffxiv_pictomancy) and the CrystalCast-specific [Wrepsk fork](https://github.com/Wrepsk/ffxiv_pictomancy), extracted at commit `ed205c725fe1d17124480823eac7affaccd669ee`. Original authorship and inline credits are retained. Ported files are marked with attribution; the upstream AGPL license text is included at `CrystalRenderer/CrystalRenderer/LICENSE.Pictomancy`.

Pictomancy is distributed under `AGPL-3.0-or-later`. CrystalRenderer remains under that license. The original Pictomancy submodule has been removed. The source links and extraction commit above record its provenance; `Pictomancy.dll` is not included in releases.

## Packaged runtime libraries

The release archive includes the runtime assemblies selected by the locked dependency graph, including Microsoft WebView2, ImageSharp, KamiToolKit, and SharpDX components. Package versions and integrity hashes are recorded in the project lock files, including `CrystalCast/packages.lock.json` and `CrystalRenderer/CrystalRenderer/packages.lock.json`; their respective upstream licenses apply.

ImageSharp remains at its existing version during the renderer migration. Its direct/transitive uses, current audit findings, and separate upgrade/removal follow-up are documented in `docs/DEPENDENCY_AUDIT.md`.

## Bundled UI fonts

The plugin UI ships three font families in `CrystalCast/Assets/Fonts`, unmodified static instances downloaded from Google Fonts. Each is licensed under the SIL Open Font License 1.1; the license text with its copyright line is included beside the fonts.

- Marcellus, Copyright (c) 2012 Brian J. Bonislawsky DBA Astigmatic (AOETI), Reserved Font Name "Marcellus" (`OFL-Marcellus.txt`)
- Hanken Grotesk, Copyright 2021 The Hanken Grotesk Project Authors (`OFL-HankenGrotesk.txt`)
- Geist Mono, Copyright 2024 The Geist Project Authors (`OFL-GeistMono.txt`)
