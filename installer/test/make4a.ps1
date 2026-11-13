# Real-size set: the 4A package as GitHub data parts, served beside its vault and the current installer.
param([string]$Package = 'F:\Unity-Tools\transplant-gate\release-2.0.0-4A-package', [string]$Out = 'F:\Unity-Tools\transplant-gate\release-gate-test\remote-4A-assets', [string]$Tools)
$ErrorActionPreference = 'Stop'
if (Test-Path $Out) { throw "exists: $Out" }
[Reflection.Assembly]::LoadFrom("$Tools\BattleTechUCUSetup.exe") | Out-Null
$clock = [Diagnostics.Stopwatch]::StartNew()
$parts = [ReleaseGate.Volumes]::WriteParts($Package, $Out, '2.0.0-4A', [long](1.95 * 1GB), $null, [Threading.CancellationToken]::None)
Copy-Item "$Package\package.vault" $Out
Copy-Item "$Tools\BattleTechUCUSetup.exe" $Out
"parts written in $([int]$clock.Elapsed.TotalSeconds) s"
Get-ChildItem $Out | ForEach-Object { '{0} {1}' -f $_.Name, $_.Length }
