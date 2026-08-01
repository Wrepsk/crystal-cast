# Contributing to CrystalCast

CrystalCast is a Windows-targeted Dalamud plugin. Contributions should preserve its local browser-rendering scope, avoid gameplay automation, and keep failures in browser or graphics integrations recoverable.

## Development setup

1. Install the .NET SDK selected by `global.json`.
2. Install or update Dalamud through XIVLauncher so its development assemblies exist under the normal Dalamud development path.
3. Clone the repository with its pinned submodule:

   ```powershell
   git clone --recurse-submodules https://github.com/Wrepsk/crystal-cast.git
   cd crystal-cast
   ```

4. Restore, build, and test:

   ```powershell
   dotnet restore CrystalCast.sln --locked-mode -p:Platform=x64
   dotnet build CrystalCast.sln -c Debug --no-restore -p:Platform=x64
   dotnet test CrystalCast.Tests/CrystalCast.Tests.csproj -c Debug --no-build --no-restore -p:Platform=x64
   ```

Add `CrystalCast/bin/x64/Debug/CrystalCast.dll` as a Dalamud development plugin for manual testing.

## Pull requests

- Keep changes focused and explain user-visible behavior and compatibility impact.
- Add or update tests for pure policies, migration, parsing, IPC, and lifecycle behavior.
- Test browser, graphics, or Wine changes manually when the required environment is available; state any untested path explicitly.
- Do not commit build output, browser profiles, local configuration, or the ignored refactor plan.
- Update `CHANGELOG.md` for user-visible changes.
- If dependencies change, run `dotnet restore CrystalCast.sln --force-evaluate` and commit the updated lock files.

CI runs the SDK's default analyzers and treats warnings in CrystalCast and its tests as errors. The pinned Pictomancy project is built as a separate project so its upstream diagnostics are not promoted by CrystalCast's warning policy.

## Release process

1. Move relevant entries from `Unreleased` into a dated version section.
2. Set the matching version in `CrystalCast/CrystalCast.csproj` and `CrystalCast/CrystalCast.json`.
3. Verify a clean Release build and package:

   ```powershell
   dotnet build CrystalCast.sln -c Release --no-restore -p:Platform=x64
   ./scripts/Verify-Package.ps1 -ExpectedVersion 0.6.0
   ```

4. Create and push a numeric tag such as `0.6.0`. The release workflow verifies the tag against the manifest, runs tests, and publishes the validated ZIP to GitHub Releases.
