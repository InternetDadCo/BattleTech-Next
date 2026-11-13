# Compares an install folder with a player folder: same relative files, same SHA-256 (the installer's own files excluded).
param([string]$Install, [string]$Player)
$skip = '.btu6-install.tsv', 'ucu-manifest.tsv'
function Map([string]$root) {
    $m = @{}
    Get-ChildItem -Recurse -File $root | ForEach-Object {
        $rel = $_.FullName.Substring($root.TrimEnd('\').Length + 1)
        if ($skip -notcontains $rel) { $m[$rel] = (Get-FileHash -Algorithm SHA256 $_.FullName).Hash }
    }
    $m
}
$a = Map $Install; $b = Map $Player
$diff = @()
foreach ($k in $a.Keys) { if (-not $b.ContainsKey($k)) { $diff += "extra $k" } elseif ($a[$k] -ne $b[$k]) { $diff += "differs $k" } }
foreach ($k in $b.Keys) { if (-not $a.ContainsKey($k)) { $diff += "missing $k" } }
if ($diff.Count -eq 0) { "EQUAL: $($a.Count) files" } else { "DIFFERENT:"; $diff }
