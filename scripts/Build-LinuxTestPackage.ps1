param(
    # Optional: build the native helper first in a Docker Ubuntu 24.04 container.
    [switch]$BuildHelper,
    [string]$OutputDirectory = "artifacts/linux-test"
)

# Builds an experimental CrystalCast test zip that includes the native Linux
# WebKitGTK helper. The normal release package and CI checks are unchanged.
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$helper = Join-Path $root "native/webkitgtk-host/build/crystalcast-webkit-host"

if ($BuildHelper) {
    docker run --rm -v "${root}:/src" -w /src/native/webkitgtk-host ubuntu:24.04 sh -c `
        "apt-get update -qq && apt-get install -y -qq --no-install-recommends build-essential pkg-config libwebkitgtk-6.0-dev libgtk-4-dev >/dev/null && make -B"
    if ($LASTEXITCODE -ne 0) { throw "Native helper build failed." }
}

if (-not (Test-Path -LiteralPath $helper -PathType Leaf)) {
    throw "Native helper not found at $helper. Run with -BuildHelper, or copy the CI 'crystalcast-webkit-host' artifact there."
}

dotnet build (Join-Path $root "CrystalCast.sln") -c Release --no-restore -p:Platform=x64
if ($LASTEXITCODE -ne 0) { throw "Plugin build failed." }

$package = Join-Path $root "CrystalCast/bin/x64/Release/CrystalCast/latest.zip"
& (Join-Path $PSScriptRoot "Verify-Package.ps1") -PackagePath $package -IncludesWebKitGtkHost

$version = ([xml](Get-Content (Join-Path $root "CrystalCast/CrystalCast.csproj"))).Project.PropertyGroup.Version | Select-Object -First 1
$destination = Join-Path $root $OutputDirectory
New-Item -ItemType Directory -Force -Path $destination | Out-Null
$target = Join-Path $destination "CrystalCast-$version-linux-webkitgtk-test.zip"
Copy-Item -LiteralPath $package -Destination $target -Force
Copy-Item -LiteralPath (Join-Path $root "docs/LINUX_WEBKITGTK_TESTING.md") -Destination $destination -Force
Write-Host "Linux test package: $target"
