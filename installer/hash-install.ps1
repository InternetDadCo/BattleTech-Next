<#
.SYNOPSIS
  Hashes a BATTLETECH install (GOG, Steam or other) so the UCU installer can be taught to accept it.

.DESCRIPTION
  Writes one text report with:
    - the installer fingerprint, computed exactly as GateCore.cs Gate.Fingerprint() does
      (paste it into ReleaseManager "Extra fingerprints" or ReleasePack --extra-fingerprint);
    - the digest of each part of that fingerprint, so a mismatch names the file that differs;
    - BattleTech.exe version info;
    - path, size and SHA-256 of every file in the install (skip with -Quick).
  The report holds paths relative to the install folder only: no user name, no drive letters.

  The fingerprint opens the package for anyone who has it. Send the report privately to the
  UCU maintainer; do not post it in a public issue or chat.

  Reads only. Nothing in the install is changed.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File hash-install.ps1
  (finds a GOG install from the registry)

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File hash-install.ps1 -Path "C:\GOG Games\BATTLETECH"

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File hash-install.ps1 -Path D:\Steam\steamapps\common\BATTLETECH -Compare gog.txt
  (hashes this install and lists files that differ from an earlier report)

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File hash-install.ps1 -Report steam.txt -Compare gog.txt
  (compares two saved reports; hashes nothing)
#>
[CmdletBinding()]
param(
    [string]$Path,
    [string]$Out,
    [switch]$Quick,
    [string]$Compare,
    [string]$Report
)

$ErrorActionPreference = 'Stop'
$ScriptVersion = 1

# Must match GateCore.cs: WholeFiles, SampledFiles, Window.
$WholeFiles = @('BattleTech.exe')
$SampledFiles = @(
    'BattleTech_Data/globalgamemanagers.assets',
    'BattleTech_Data/resources.assets',
    'BattleTech_Data/resources.assets.resS',
    'BattleTech_Data/sharedassets0.assets',
    'BattleTech_Data/level10',
    'BattleTech_Data/level10.resS'
)
$Window = 1 -shl 20

function Hex([byte[]]$b) { -join ($b | ForEach-Object { $_.ToString('x2') }) }

function Find-GogInstall {
    foreach ($root in 'HKLM:\SOFTWARE\WOW6432Node\GOG.com\Games', 'HKLM:\SOFTWARE\GOG.com\Games') {
        if (-not (Test-Path $root)) { continue }
        foreach ($k in Get-ChildItem $root) {
            $p = Get-ItemProperty $k.PSPath
            if ("$($p.gameName)" -match 'battletech' -and $p.path -and (Test-Path (Join-Path $p.path 'BattleTech.exe'))) {
                return $p.path
            }
        }
    }
    foreach ($guess in 'C:\GOG Games\BATTLETECH', 'C:\Program Files (x86)\GOG Galaxy\Games\BATTLETECH') {
        if (Test-Path (Join-Path $guess 'BattleTech.exe')) { return $guess }
    }
    return $null
}

function Get-Fingerprint([string]$root) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $ms = New-Object System.IO.MemoryStream
    $parts = New-Object System.Collections.Generic.List[string]

    $add = {
        param([string]$label, [byte[]]$digest)
        $l = [System.Text.Encoding]::UTF8.GetBytes($label.ToLowerInvariant())
        $ms.Write($l, 0, $l.Length)
        $ms.Write($digest, 0, $digest.Length)
        $parts.Add("$($label.ToLowerInvariant())`t$(Hex $digest)")
    }

    foreach ($rel in $WholeFiles) {
        $p = Join-Path $root $rel
        if (-not (Test-Path -LiteralPath $p)) { throw "Retail file not found: $rel" }
        & $add $rel ($sha.ComputeHash([System.IO.File]::ReadAllBytes($p)))
    }
    foreach ($rel in $SampledFiles) {
        $p = Join-Path $root $rel
        if (-not (Test-Path -LiteralPath $p)) { throw "Retail file not found: $rel" }
        $f = New-Object System.IO.FileStream($p, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
        try {
            [long]$len = $f.Length
            $buf = New-Object byte[] $Window
            # C# long division truncates; shift keeps it integral (len is never negative).
            $starts = @([long]0, [Math]::Max([long]0, ($len -shr 1) - ($Window -shr 1)), [Math]::Max([long]0, $len - $Window))
            foreach ($at in $starts) {
                $f.Position = $at
                $got = 0
                while ($got -lt $Window) {
                    $n = $f.Read($buf, $got, $Window - $got)
                    if ($n -le 0) { break }
                    $got += $n
                }
                & $add ("{0}@{1}/{2}" -f $rel, $at, $len) ($sha.ComputeHash($buf, 0, $got))
            }
        } finally { $f.Dispose() }
    }
    $fp = Hex ($sha.ComputeHash($ms.ToArray()))
    return @{ Fingerprint = $fp; Parts = $parts }
}

function Read-Manifest([string]$file) {
    $map = @{}
    $in = $false
    foreach ($line in Get-Content -LiteralPath $file) {
        if ($line -eq '[files]') { $in = $true; continue }
        if ($line.StartsWith('[') -and $line.EndsWith(']')) { $in = $false; continue }
        if ($in -and $line -and -not $line.StartsWith('#')) {
            $c = $line -split "`t"
            if ($c.Count -ge 3) { $map[$c[0]] = "$($c[1])`t$($c[2])" }
        }
    }
    return $map
}

function Read-Section([string]$file, [string]$name) {
    $in = $false
    foreach ($line in Get-Content -LiteralPath $file) {
        if ($line.StartsWith('[') -and $line.EndsWith(']')) { $in = ($line -eq "[$name]"); continue }
        if ($in -and $line) { $line }
    }
}

function Compare-Reports([string]$here, [string]$there) {
    $fa = @(Read-Section $here 'fingerprint')[0]
    $fb = @(Read-Section $there 'fingerprint')[0]
    Write-Host ''
    Write-Host ("Fingerprint: {0}" -f $(if ($fa -eq $fb) { 'same' } else { 'differs' }))
    $pa = @{}; foreach ($l in Read-Section $here 'fingerprint-parts') { $c = $l -split "`t"; $pa[$c[0]] = $c[1] }
    $pb = @{}; foreach ($l in Read-Section $there 'fingerprint-parts') { $c = $l -split "`t"; $pb[$c[0]] = $c[1] }
    foreach ($k in $pa.Keys + $pb.Keys | Sort-Object -Unique) {
        if ($pa[$k] -ne $pb[$k]) { Write-Host "  part differs: $k" }
    }

    $a = Read-Manifest $here
    $b = Read-Manifest $there
    if ($a.Count -eq 0 -or $b.Count -eq 0) { Write-Warning 'File lists need both reports made without -Quick.'; return }
    $only1 = @($a.Keys | Where-Object { -not $b.ContainsKey($_) } | Sort-Object)
    $only2 = @($b.Keys | Where-Object { -not $a.ContainsKey($_) } | Sort-Object)
    $diff = @($a.Keys | Where-Object { $b.ContainsKey($_) -and $a[$_] -ne $b[$_] } | Sort-Object)
    $same = $a.Count - $only1.Count - $diff.Count
    Write-Host ("Files: {0} same, {1} differ, {2} only in {3}, {4} only in {5}" -f $same, $diff.Count, $only1.Count, (Split-Path $here -Leaf), $only2.Count, (Split-Path $there -Leaf))
    foreach ($r in $diff) { Write-Host "  differ     $r" }
    foreach ($r in $only1) { Write-Host "  only here  $r" }
    foreach ($r in $only2) { Write-Host "  only there $r" }
}

if ($Report) {
    if (-not $Compare) { throw '-Report needs -Compare <other report>.' }
    Compare-Reports (Resolve-Path -LiteralPath $Report).ProviderPath (Resolve-Path -LiteralPath $Compare).ProviderPath
    return
}

# --- locate the install ---
if (-not $Path) {
    $Path = Find-GogInstall
    if (-not $Path) { throw 'No GOG BATTLETECH install found. Pass the folder with -Path "<folder holding BattleTech.exe>".' }
}
$Path = (Resolve-Path -LiteralPath $Path).ProviderPath.TrimEnd('\')
if ((Split-Path $Path -Leaf) -eq 'BattleTech_Data') { $Path = Split-Path $Path -Parent }
if (-not (Test-Path (Join-Path $Path 'BattleTech.exe'))) { throw "BattleTech.exe not found in $Path" }

$store = 'unknown'
if (Get-ChildItem -LiteralPath $Path -Filter 'goggame-*.info' -ErrorAction SilentlyContinue) { $store = 'gog' }
elseif (Test-Path (Join-Path $Path 'BattleTech_Data\Plugins\steam_api64.dll')) { $store = 'steam (steam_api64.dll present)' }
if (-not $Out) { $Out = "battletech-{0}-hashes.txt" -f ($store -split ' ')[0] }
# .NET file calls resolve relative paths against the process directory, not the PowerShell location.
$Out = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Out)
if ($Compare) { $Compare = (Resolve-Path -LiteralPath $Compare).ProviderPath }

Write-Host "Install: $Path"
Write-Host "Store:   $store"
Write-Host 'Computing installer fingerprint...'
$fp = Get-Fingerprint $Path

$exe = (Get-Item -LiteralPath (Join-Path $Path 'BattleTech.exe')).VersionInfo
$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine("# BATTLETECH install hashes (hash-install.ps1 v$ScriptVersion)")
[void]$sb.AppendLine('# Private: the fingerprint below opens the UCU package. Send this file to the maintainer only.')
[void]$sb.AppendLine('[install]')
[void]$sb.AppendLine("store`t$store")
[void]$sb.AppendLine("exe_file_version`t$($exe.FileVersion)")
[void]$sb.AppendLine("exe_product_version`t$($exe.ProductVersion)")
foreach ($info in Get-ChildItem -LiteralPath $Path -Filter 'goggame-*.info' -ErrorAction SilentlyContinue) {
    try {
        $j = Get-Content -LiteralPath $info.FullName -Raw | ConvertFrom-Json
        [void]$sb.AppendLine("gog_info`t$($info.Name)`tgameId=$($j.gameId) buildId=$($j.buildId) version=$($j.version) language=$($j.language)")
    } catch { [void]$sb.AppendLine("gog_info`t$($info.Name)`t(unreadable)") }
}
[void]$sb.AppendLine('[fingerprint]')
[void]$sb.AppendLine($fp.Fingerprint)
[void]$sb.AppendLine('[fingerprint-parts]')
foreach ($p in $fp.Parts) { [void]$sb.AppendLine($p) }

if (-not $Quick) {
    $files = Get-ChildItem -LiteralPath $Path -Recurse -File -Force | Sort-Object FullName
    $total = ($files | Measure-Object Length -Sum).Sum
    Write-Host ('Hashing {0} files, {1:N1} GiB (use -Quick to skip)...' -f $files.Count, ($total / 1GB))
    [void]$sb.AppendLine('[files]')
    [void]$sb.AppendLine("# path`tsize`tsha256")
    $done = [long]0
    $i = 0
    foreach ($f in $files) {
        $i++
        $rel = $f.FullName.Substring($Path.Length + 1).Replace('\', '/')
        Write-Progress -Activity 'Hashing install' -Status $rel -PercentComplete ([int](100 * $done / [Math]::Max([long]1, [long]$total)))
        $h = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        [void]$sb.AppendLine("$rel`t$($f.Length)`t$h")
        $done += $f.Length
    }
    Write-Progress -Activity 'Hashing install' -Completed
}

[System.IO.File]::WriteAllText($Out, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
Write-Host ''
Write-Host "Fingerprint: $($fp.Fingerprint)"
Write-Host "Report:      $Out"
Write-Host 'Send the report privately to the UCU maintainer. Do not post it publicly.'

if ($Compare) { Compare-Reports $Out $Compare }
