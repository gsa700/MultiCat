# Publishes MultiCAT as a self-contained win-x64 release and zips it.
# Usage:  pwsh packaging/build-release.ps1
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$version = ([xml](Get-Content "$repo/Directory.Build.props")).Project.PropertyGroup.Version
$stage = "$repo/dist/MultiCAT-$version-win-x64"

Write-Host "Building MultiCAT $version ..."
Remove-Item "$repo/dist" -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $stage | Out-Null

$common = '-c', 'Release', '-r', 'win-x64', '--self-contained',
          '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
          '--nologo', '-v', 'quiet'

foreach ($proj in 'MultiCat.Service', 'MultiCat.Gui') {
    Write-Host "  publishing $proj"
    dotnet publish "$repo/src/$proj" @common -o "$stage/$proj"
}

# The OmniRig server targets net48 (the only framework whose COM wrapper serves a
# working late-bound IDispatch), so it can't be self-contained or single-file —
# it runs on the .NET Framework built into Windows.
Write-Host "  publishing MultiCat.OmniRig (net48)"
dotnet publish "$repo/src/MultiCat.OmniRig" -c Release --nologo -v quiet -o "$stage/MultiCat.OmniRig"

# Flatten the OmniRig register/unregister helpers next to its exe.
Copy-Item "$repo/packaging/Register OmniRig.cmd"   "$stage/MultiCat.OmniRig/"
Copy-Item "$repo/packaging/Unregister OmniRig.cmd" "$stage/MultiCat.OmniRig/"

# Top-level user files.
Copy-Item "$repo/packaging/Start MultiCAT.cmd" $stage
Copy-Item "$repo/packaging/QUICKSTART.txt" $stage
Copy-Item "$repo/packaging/appsettings.example-real-radio.json" $stage
Copy-Item "$repo/LICENSE" $stage

$zip = "$repo/dist/MultiCAT-$version-win-x64.zip"
# Pack the CONTENTS, not the staging folder itself: extracting the zip already
# creates a folder, and including one here buries everything a second level down.
Compress-Archive -Path "$stage/*" -DestinationPath $zip -Force
$mb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "Release ready: $zip ($mb MB)"
