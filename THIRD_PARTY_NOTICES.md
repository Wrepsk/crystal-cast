# Third-party notices

CrystalCast depends on third-party libraries whose own licenses continue to apply.

## Pictomancy

CrystalCast builds the `ThirdParty/ffxiv_pictomancy` Git submodule as a project reference. The submodule points to the `Wrepsk/ffxiv_pictomancy` fork, derived from [sourpuh/ffxiv_pictomancy](https://github.com/sourpuh/ffxiv_pictomancy), because CrystalCast requires project-specific scene-composite and backbuffer changes that are not present in the upstream branch. Git pins the exact source commit used by each CrystalCast revision.

Pictomancy is distributed under `AGPL-3.0-or-later`. Its license is included at `ThirdParty/ffxiv_pictomancy/LICENSE`.

## Packaged runtime libraries

The release archive includes the runtime assemblies selected by the locked dependency graph, including Microsoft WebView2, ImageSharp, KamiToolKit, and SharpDX components. Package versions and integrity hashes are recorded in `CrystalCast/packages.lock.json`; their respective upstream licenses apply.
