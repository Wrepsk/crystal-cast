# Dependency audit: ImageSharp removed

CrystalCast and CrystalRenderer no longer depend on SixLabors.ImageSharp. A normal
audited restore (`dotnet restore CrystalCast.sln --locked-mode -p:Platform=x64`)
passes again, with NuGet auditing and warnings-as-errors unchanged.

## What happened

ImageSharp **3.1.12** reached the dependency graph two ways:

- **CrystalCast** decoded the JPEG screenshots WebView2 returns in JPEG capture mode
  (`CrystalCast/Video/WebView2JpegFrameCapture.cs`). That mode is the Windows capture
  fallback and the WebView2 path under Wine.
- **CrystalRenderer** referenced KamiToolKit **1.1.2**, which declares ImageSharp
  3.1.12 as a dependency. CrystalRenderer itself never called ImageSharp.

NuGet then reported these advisories, failing restore under warnings-as-errors:
GHSA-gwg2-r3hj-4w44, GHSA-j3p4-wp97-rph4, GHSA-j9gm-c75j-xc9q, GHSA-jjfr-hcj7-qf5w,
GHSA-v76p-62qx-wwq2 and GHSA-wmxv-xphr-5c9g. All are fixed in ImageSharp 3.2.0 and
4.1.2, but those versions validate a Six Labors license key during Release builds.

## Resolution

- **JPEG decoding uses WIC.** `CrystalCast/Video/WicBgraDecoder.cs` decodes with the
  Windows Imaging Component, which Windows and Wine both provide, through its COM
  interfaces directly. It produces the same 32-bit BGRA frames as before.
  `CrystalCast.Tests/WicBgraDecoderTests.cs` checks decoding, reuse and invalid data.
- **KamiToolKit is 2.2.66.** That release has no package dependencies. CrystalRenderer's
  port only follows renamed namespaces (`KamiToolKit.UiOverlay`,
  `KamiToolKit.Nodes.Simplified`), the single-argument node `Dispose`, and the
  asynchronous `KamiToolKitLibrary.InitializeAsync`. Until that finishes, or if it fails,
  frames use the existing ImGui-overlay fallback.

No advisories are suppressed, no audit settings are relaxed, and no license checks are
bypassed. The release package no longer contains `SixLabors.ImageSharp.dll`.

## Still to verify in game

- The **Native overlay** output layer (KamiToolKit) on Windows and under Wine.
- **WebView2 JPEG capture** (WIC decoding) on Windows and under Wine.
