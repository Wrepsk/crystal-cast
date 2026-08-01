param(
    [string]$PackagePath = "CrystalCast/bin/x64/Release/CrystalCast/latest.zip",
    [string]$ExpectedVersion = ""
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) {
    throw "Release package not found: $PackagePath"
}

Add-Type -AssemblyName System.IO.Compression
$archive = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $PackagePath))

try {
    $expectedFiles = @(
        "CrystalCast.deps.json",
        "CrystalCast.dll",
        "CrystalCast.json",
        "KamiToolKit.dll",
        "Microsoft.Web.WebView2.Core.dll",
        "Pictomancy.dll",
        "SharpDX.D3DCompiler.dll",
        "SharpDX.Direct2D1.dll",
        "SharpDX.Direct3D11.dll",
        "SharpDX.dll",
        "SharpDX.DXGI.dll",
        "SharpDX.Mathematics.dll",
        "SixLabors.ImageSharp.dll",
        "WebView2Loader.dll"
    )

    $actualFiles = @($archive.Entries | ForEach-Object FullName | Sort-Object)
    $unexpected = @($actualFiles | Where-Object { $_ -notin $expectedFiles })
    $missing = @($expectedFiles | Where-Object { $_ -notin $actualFiles })

    if ($unexpected.Count -gt 0) {
        throw "Unexpected package entries: $($unexpected -join ', ')"
    }

    if ($missing.Count -gt 0) {
        throw "Missing package entries: $($missing -join ', ')"
    }

    if ($actualFiles.Count -ne ($actualFiles | Select-Object -Unique).Count) {
        throw "The package contains duplicate entries."
    }

    $manifestEntry = $archive.GetEntry("CrystalCast.json")
    $reader = [System.IO.StreamReader]::new($manifestEntry.Open())
    try {
        $manifest = $reader.ReadToEnd() | ConvertFrom-Json
    }
    finally {
        $reader.Dispose()
    }

    if ($manifest.InternalName -ne "CrystalCast") {
        throw "Unexpected manifest InternalName: $($manifest.InternalName)"
    }

    if ($manifest.Author -ne "Wrepsk") {
        throw "Unexpected manifest author: $($manifest.Author)"
    }

    if ($manifest.DalamudApiLevel -ne 15) {
        throw "Unexpected Dalamud API level: $($manifest.DalamudApiLevel)"
    }

    if ($manifest.ApplicableVersion -ne "any") {
        throw "Unexpected applicable game version: $($manifest.ApplicableVersion)"
    }

    if ($manifest.LoadRequiredState -ne 0 -or $manifest.LoadSync -ne $false -or $manifest.CanUnloadAsync -ne $false) {
        throw "Unexpected plugin loading compatibility flags."
    }

    if ($manifest.RepoUrl -ne "https://github.com/Wrepsk/crystal-cast") {
        throw "Unexpected manifest repository URL: $($manifest.RepoUrl)"
    }

    if ($ExpectedVersion) {
        $tagVersion = [Version]$ExpectedVersion
        $assemblyVersion = [Version]$manifest.AssemblyVersion
        $tagParts = @($tagVersion.Major, $tagVersion.Minor, [Math]::Max(0, $tagVersion.Build), [Math]::Max(0, $tagVersion.Revision))
        $assemblyParts = @($assemblyVersion.Major, $assemblyVersion.Minor, [Math]::Max(0, $assemblyVersion.Build), [Math]::Max(0, $assemblyVersion.Revision))
        if (Compare-Object $tagParts $assemblyParts -SyncWindow 0) {
            throw "Tag version $ExpectedVersion does not match assembly version $($manifest.AssemblyVersion)."
        }
    }

    Write-Host "Verified $PackagePath ($($actualFiles.Count) files, version $($manifest.AssemblyVersion))."
}
finally {
    $archive.Dispose()
}
