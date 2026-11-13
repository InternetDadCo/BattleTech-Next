# Builds ReleasePack.exe, BattleTechUCUSetup.exe and ReleaseManager.exe into tools/release-gate/bin (ignored).
# Targets the .NET Framework and WinForms that ship with Windows, so the installer needs no runtime. Roslyn comes from the .NET SDK.
#   -Deploy <folder>   also copies the three exes there. Run them from that folder: on this workstation an exe in tools/ can't write.
# Reproducible: one pinned SDK (its Roslyn), -deterministic, no debug info and paths mapped, so the same commit builds the
# same bytes here and in the public repository's CI (ci/installer.yml), whose attestation the Release Manager checks.
param([string]$Deploy)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$sdkVersion = '10.0.403'   # keep equal to dotnet-version in ci/installer.yml
$sdk = (& dotnet --list-sdks | Where-Object { $_ -like "$sdkVersion *" } | Select-Object -First 1) -replace '^(\S+) \[(.*)\]$', '$2\$1'
if (-not $sdk) { throw ".NET SDK $sdkVersion is needed (dotnet --list-sdks): install that exact version." }
$csc = Join-Path $sdk 'Roslyn\bincore\csc.dll'
$fx = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$bin = Join-Path $here 'bin'
$src = Join-Path $here 'src'
New-Item -ItemType Directory -Force $bin | Out-Null
$common = @('-nologo', '-noconfig', '-nostdlib+', '-platform:x64', '-optimize+', '-langversion:7.3', '-deterministic+', '-debug-', "-pathmap:$here=installer")
$refs = 'mscorlib', 'System', 'System.Core', 'System.Drawing', 'System.Windows.Forms' | ForEach-Object { "-r:$fx\$_.dll" }

& dotnet $csc @common -target:exe "-out:$bin\ReleasePack.exe" -define:PACK @refs "$src\AssemblyInfo.cs" "$src\GateCore.cs" "$src\PublicManifest.cs" "$src\Pack.cs"
if ($LASTEXITCODE -ne 0) { throw 'csc failed for ReleasePack' }
& dotnet $csc @common -target:winexe "-win32manifest:$src\app.manifest" "-resource:$src\banner.jpg,banner.jpg" "-out:$bin\BattleTechUCUSetup.exe" -define:SETUP @refs "-r:$fx\System.Web.Extensions.dll" "$src\AssemblyInfo.cs" "$src\GateCore.cs" "$src\PublicManifest.cs" "$src\Volumes.cs" "$src\Remote.cs" "$src\Unlock.cs" "$src\SteamTicket.cs" "$src\Installer.cs" "$src\SetupForm.cs" "$src\Setup.cs"
if ($LASTEXITCODE -ne 0) { throw 'csc failed for BattleTechUCUSetup' }
& dotnet $csc @common -target:winexe "-win32manifest:$src\app.manifest" '-main:ReleaseGate.ManagerProgram' "-out:$bin\ReleaseManager.exe" -define:MANAGER @refs "-r:$fx\System.Web.Extensions.dll" "$src\AssemblyInfo.cs" "$src\GateCore.cs" "$src\PublicManifest.cs" "$src\Pack.cs" "$src\Volumes.cs" "$src\Remote.cs" "$src\Unlock.cs" "$src\SteamTicket.cs" "$src\Installer.cs" "$src\GitHubReleases.cs" "$src\ReleaseManager.cs"
if ($LASTEXITCODE -ne 0) { throw 'csc failed for ReleaseManager' }
Get-ChildItem $bin -Filter *.exe | ForEach-Object { "$($_.Name) $($_.Length)" }
if ($Deploy) {
    New-Item -ItemType Directory -Force $Deploy | Out-Null
    Copy-Item "$bin\ReleasePack.exe", "$bin\BattleTechUCUSetup.exe", "$bin\ReleaseManager.exe", "$here\verify.ps1" $Deploy -Force
    # The unlock server's files (keyserver/README.md). wrangler can't run under tools/ here, so it runs from this copy, which
    # keeps its own wrangler.toml: that one holds the database id.
    $ks = Join-Path $Deploy 'keyserver'
    New-Item -ItemType Directory -Force (Join-Path $ks 'src') | Out-Null
    Copy-Item "$here\keyserver\src\worker.js" (Join-Path $ks 'src') -Force
    Copy-Item "$here\keyserver\schema.sql", "$here\keyserver\admin.ps1", "$here\keyserver\package.json", "$here\keyserver\package-lock.json", "$here\keyserver\README.md", "$here\keyserver\.gitignore" $ks -Force
    if (Test-Path (Join-Path $ks 'wrangler.toml')) { "kept $ks\wrangler.toml" } else { Copy-Item "$here\keyserver\wrangler.toml" $ks }
    "deployed to $Deploy"
}
