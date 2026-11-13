# Builds the synthetic remote-update test set: two small players (v1, v2), their packages, and v2's data parts to serve.
param([string]$Root, [string]$Retail = 'D:\Steam\steamapps\common\BATTLETECH', [string]$Bin = 'F:\Unity-Tools\BattleTech-Unity6\tools\release-gate\bin')
$ErrorActionPreference = 'Stop'
if (Test-Path $Root) { Remove-Item -Recurse -Force $Root }
New-Item -ItemType Directory -Force "$Root\tools" | Out-Null
Copy-Item "$Bin\*.exe" "$Root\tools"

function Rand([string]$path, [int]$size, [int]$seed) {
    $r = New-Object System.Random $seed
    $b = New-Object byte[] $size
    $r.NextBytes($b)
    New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
    [IO.File]::WriteAllBytes($path, $b)
}

$p1 = "$Root\p1"
New-Item -ItemType Directory -Force "$p1\BattleTech_Data\sub" | Out-Null
Copy-Item "$Retail\BattleTech.exe" $p1
[IO.File]::WriteAllText("$p1\steam_appid.txt", '637090')
Rand "$p1\BattleTech_Data\a.bin" (2MB) 1
Rand "$p1\BattleTech_Data\b.bin" (3MB) 2
Rand "$p1\BattleTech_Data\c.bin" (100KB) 3
Rand "$p1\BattleTech_Data\same.bin" (1MB) 4
Rand "$p1\BattleTech_Data\sub\d.bin" (500KB) 5
Rand "$p1\BattleTech_Data\dup1.bin" (300KB) 6
Copy-Item "$p1\BattleTech_Data\dup1.bin" "$p1\BattleTech_Data\sub\dup2.bin"
Rand "$p1\BattleTech_Data\big.bin" (12MB) 7

$p2 = "$Root\p2"
Copy-Item -Recurse $p1 $p2
Rand "$p2\BattleTech_Data\a.bin" (2MB) 11
Remove-Item "$p2\BattleTech_Data\c.bin"
Rand "$p2\BattleTech_Data\e.bin" (4MB) 8

foreach ($v in 1, 2) {
    & "$Root\tools\ReleasePack.exe" --player "$Root\p$v" --retail $Retail --key "$Root\test-key.txt" --out "$Root\pkg$v"
    if ($LASTEXITCODE -ne 0) { throw "pack $v failed" }
}
[Reflection.Assembly]::LoadFrom("$Root\tools\BattleTechUCUSetup.exe") | Out-Null
$parts = [ReleaseGate.Volumes]::WriteParts("$Root\pkg2", "$Root\serve2", 'test2', 16MB, $null, [Threading.CancellationToken]::None)
Copy-Item "$Root\pkg2\package.vault", "$Root\pkg2\BattleTechUCUSetup.exe" "$Root\serve2"
Get-ChildItem "$Root\serve2" | ForEach-Object { '{0} {1}' -f $_.Name, $_.Length }
