param(
    [string]$Distribution = "Ubuntu",
    [string]$Container = "crystalcast-wpe-dev",
    [int]$Seconds = 15,
    [int]$Width = 1280,
    [int]$Height = 720,
    [string]$Provider = "GenericWeb",
    [string]$Url = "",
    [string]$Controls = "true",
    [switch]$CheckBackend,
    [string]$Output = "NativeLinuxBrowser/artifacts/csharp-video-tcp.json"
)
$ErrorActionPreference = "Stop"
$probeTempDirectory = Join-Path $env:LOCALAPPDATA ("Temp\CrystalCast-NativeProbe-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $probeTempDirectory | Out-Null
$readyPath = Join-Path $probeTempDirectory 'ready.jsonl'
$errorPath = Join-Path $probeTempDirectory 'runner.log'
$endpointPath = Join-Path $probeTempDirectory 'endpoint.json'
$runner = Start-Process wsl.exe -WindowStyle Hidden -PassThru -RedirectStandardOutput $readyPath -RedirectStandardError $errorPath -ArgumentList @(
    '-d', $Distribution, '-u', 'root', '--', 'podman', 'exec', '--user', '1000', '-w', '/work/NativeLinuxBrowser',
    '--env', 'GST_PLUGIN_FEATURE_RANK=fakeaudiosink:1000', $Container, 'python', 'tests/serve_probe.py'
)
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    $ready = $null
    while (-not $ready) {
        if ($runner.HasExited -or [DateTime]::UtcNow -gt $deadline) {
            throw "Native probe startup failed: $(Get-Content -LiteralPath $errorPath -Raw)"
        }
        $firstLine = Get-Content -LiteralPath $readyPath -TotalCount 1 -ErrorAction SilentlyContinue
        if ($firstLine) { $ready = $firstLine | ConvertFrom-Json }
        if (-not $ready) { Start-Sleep -Milliseconds 50 }
    }
    # The endpoint credential stays in the user's private Windows temp folder.
    $nativeDestination = '/mnt/c/' + $endpointPath.Substring(3).Replace('\', '/')
    & wsl -d $Distribution -u root -- podman cp ($Container + ':' + $ready.endpoint) $nativeDestination
    if ($LASTEXITCODE -ne 0) { throw 'Could not copy the development endpoint.' }
    $probeUrl = if ($Url) { $Url } else { $ready.url }
    & dotnet run --project tools/CrystalCast.LinuxBrowserProbe -c Debug --no-build --no-restore -p:Platform=x64 -- `
        --endpoint-file $endpointPath --check-backend $CheckBackend.IsPresent.ToString().ToLowerInvariant() --provider $Provider --url $probeUrl --seconds $Seconds --controls $Controls --width $Width --height $Height --output $Output
    $probeExitCode = $LASTEXITCODE
    if (-not $runner.WaitForExit(10000)) { throw 'The helper did not stop after the C# consumer disconnected.' }
    Get-Content -LiteralPath $readyPath | Select-Object -Last 1
    if ($probeExitCode -ne 0) { throw "C# browser probe failed ($probeExitCode)." }
}
finally {
    if (Test-Path -LiteralPath $endpointPath) { Remove-Item -LiteralPath $endpointPath }
    $runner.Dispose()
}
