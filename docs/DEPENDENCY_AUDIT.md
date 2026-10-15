# Existing ImageSharp dependency and audit findings

The CrystalRenderer migration retains ImageSharp **3.1.12** and all other existing dependency versions. No Six Labors license key or account configuration is added. A trial of 4.1.2 was reverted: its Release-build license validation requires a key, and dependency upgrades are outside this migration's scope.

## Why it is present

`CrystalCast/Video/WebView2JpegFrameCapture.cs` is CrystalCast's only direct ImageSharp call site. WebView2 writes a JPEG screenshot to a reusable stream; `Image.Load<Bgra32>` decodes it, and `CopyPixelDataTo` fills the pooled BGRA `VideoFrame` uploaded to the screen texture. This path is the Windows capture fallback and the required browser capture path under Wine. Removing the decoder without a replacement would break those modes.

KamiToolKit **1.1.2** also declares ImageSharp **3.1.12** as a dependency. CrystalRenderer uses KamiToolKit's native background overlay and `SimpleImageNode` to present an already-created D3D texture. CrystalRenderer has no direct ImageSharp call sites or package reference. Removing CrystalCast's JPEG decoder dependency alone would still leave ImageSharp in the runtime dependency graph through KamiToolKit.

Replacing the JPEG decoder and the native-overlay dependency is feasible follow-up work, but requires codec/platform compatibility testing and changes beyond extracting the existing renderer. It is not a simple unused-package removal.

## Current NuGet advisories

A fresh audited restore reports these existing ImageSharp advisories. Consult their upstream records for current affected/fixed ranges:

| Advisory | Affected functionality |
| --- | --- |
| [GHSA-j9gm-c75j-xc9q](https://github.com/advisories/GHSA-j9gm-c75j-xc9q) | TIFF CCITT T4 encoding can write beyond its output buffer |
| [GHSA-jjfr-hcj7-qf5w](https://github.com/advisories/GHSA-jjfr-hcj7-qf5w) | TIFF CCITT T6 encoding can write beyond its output buffer |
| [GHSA-j3p4-wp97-rph4](https://github.com/advisories/GHSA-j3p4-wp97-rph4) | Histogram equalization uses an unchecked luminance-derived index |
| [GHSA-wmxv-xphr-5c9g](https://github.com/advisories/GHSA-wmxv-xphr-5c9g) | Malformed BigTIFF entry counts can stall decoding |
| [GHSA-gwg2-r3hj-4w44](https://github.com/advisories/GHSA-gwg2-r3hj-4w44) | ICC CLUT parsing can allocate from unvalidated dimensions |

The direct CrystalCast path decodes locally generated WebView2 JPEGs. It does not perform TIFF encoding, histogram equalization, or access ICC profile entries. That limits the direct exposure of this call site, but is not a claim that the dependency is generally safe or that every transitive path has been audited.

## Build impact and follow-up

Warnings-as-errors makes the normal audited restore fail on NU1902/NU1903. The migration does not suppress these advisories, disable NuGet auditing in project files or CI, or relax the warning policy. For local migration validation only, assets were restored with the one-time `-p:NuGetAudit=false` argument, then both configurations were built/tested with `--no-restore`. This validates the migrated code and package, not a clean security audit.

Resolve the audit separately by obtaining the appropriate free community license key, verifying API/runtime compatibility with KamiToolKit and JPEG capture, and upgrading to a currently patched ImageSharp version. Alternatively, replace both JPEG decoding and the native-overlay dependency, with Windows/Wine coverage. User eligibility and key provisioning should be confirmed through Six Labors; no license-key checks should be bypassed.
