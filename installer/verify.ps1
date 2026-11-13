# Checks a BattleTech UCU install against a release's public manifest (ucu-manifest.tsv), with no help from the installer.
#   powershell -ExecutionPolicy Bypass -File verify.ps1 -Install "D:\Games\BATTLETECH-UCU" [-Manifest <file or https URL>]
# With no -Manifest it reads the copy the installer left in the install folder; compare that file's SHA-256 (printed below)
# with the one in the release notes. Exit code 0 = every listed file matches; 1 = something differs.
param(
    [Parameter(Mandatory = $true)][string]$Install,
    [string]$Manifest
)
$ErrorActionPreference = 'Stop'
if (-not $Manifest) { $Manifest = Join-Path $Install 'ucu-manifest.tsv' }
if ($Manifest -match '^https://') {
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ('ucu-manifest-' + [guid]::NewGuid() + '.tsv')
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    (New-Object Net.WebClient).DownloadFile($Manifest, $tmp)
    $Manifest = $tmp
}
"Manifest: $Manifest"
"Manifest SHA-256 (compare with the release notes): $((Get-FileHash -Algorithm SHA256 -LiteralPath $Manifest).Hash.ToLowerInvariant())"

$rows = Get-Content -LiteralPath $Manifest | Where-Object { $_ -and -not $_.StartsWith('#') } | ForEach-Object {
    $c = $_.Split("`t")
    [pscustomobject]@{ Path = $c[0]; Size = [long]$c[1]; Sha512 = $c[2]; Origin = $c[3]; Exec = $c[4] }
}
$bad = 0; $n = 0
foreach ($r in $rows) {
    $n++
    $f = Join-Path $Install $r.Path
    if (-not (Test-Path -LiteralPath $f -PathType Leaf)) { "MISSING  $($r.Path)"; $bad++; continue }
    if ((Get-Item -LiteralPath $f).Length -ne $r.Size) { "SIZE     $($r.Path)"; $bad++; continue }
    if ((Get-FileHash -Algorithm SHA512 -LiteralPath $f).Hash -ne $r.Sha512) { "HASH     $($r.Path)"; $bad++ }
}
$counts = $rows | Group-Object Origin | ForEach-Object { "$($_.Name) $($_.Count)" }
$exec = $rows | Where-Object { $_.Exec -eq 'exec' } | Group-Object Origin | ForEach-Object { "$($_.Name) $($_.Count)" }
"Checked $n files ($($counts -join ', ')); executables by origin: $($exec -join ', ')."
if ($bad -gt 0) { "$bad file(s) differ from the manifest."; exit 1 }
'Every file matches the manifest.'
exit 0
