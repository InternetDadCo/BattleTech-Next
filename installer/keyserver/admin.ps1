# Runs the unlock server's admin tasks against its D1 database through wrangler (../docs/keyserver-design.md).
#   admin.ps1 init                     creates the tables (safe to repeat)
#   admin.ps1 new-code <name>          makes an access code, prints it once, adds it to <Keys>\access-codes.tsv
#   admin.ps1 revoke <name>            that code stops working        admin.ps1 restore <name>   it works again
#   admin.ps1 codes                    every code's name, state and unlock count
#   admin.ps1 log [<name>]             the last 50 unlock attempts, all or one code's
#   admin.ps1 retire <release id>      the server refuses that release    admin.ps1 unretire <release id>
#   admin.ps1 close [<message>]        switches installing off, showing the message to players
#   admin.ps1 open                     switches it back on               admin.ps1 status
#   admin.ps1 steam                    Steam route: accounts seen, borrowed (Family Sharing), blocked, unlocks this week
#   admin.ps1 steam-block <steam id>   that Steam account (64-bit id, 7656...) can't unlock   admin.ps1 steam-unblock <id>
#   admin.ps1 steam-off [<message>]    switches the Steam route off (codes still work)       admin.ps1 steam-on
#   admin.ps1 family allow|deny        whether Family Sharing borrowers may unlock (default allow)
# -Local works on the database `npx wrangler dev` uses, for tests. Run from any folder; wrangler runs in this one.
param(
    [Parameter(Mandatory = $true, Position = 0)][ValidateSet('init', 'new-code', 'revoke', 'restore', 'codes', 'log', 'retire', 'unretire', 'close', 'open', 'status', 'steam', 'steam-block', 'steam-unblock', 'steam-off', 'steam-on', 'family')][string]$Command,
    [Parameter(Position = 1)][string]$Arg,
    [switch]$Local,
    [string]$Keys = 'F:\Unity-Tools\ReleaseManager\keys'
)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$where = if ($Local) { '--local' } else { '--remote' }
# wrangler from this folder's node_modules (npm install), else npx's. Called with $ErrorActionPreference Continue:
# PowerShell 5.1 turns a native tool's stderr lines into errors, and wrangler and npx write notices there.
$wrangler = Join-Path $here 'node_modules\.bin\wrangler.cmd'
if (-not (Test-Path $wrangler)) { $wrangler = 'npx.cmd'; $pre = @('wrangler') } else { $pre = @() }

function Wrangler([string[]]$a) {
    $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    Push-Location $here
    $errFile = [IO.Path]::GetTempFileName()
    try { $out = & $wrangler @pre @a 2> $errFile; $code = $LASTEXITCODE }
    finally { Pop-Location; $ErrorActionPreference = $old }
    if ($code -ne 0) {
        # wrangler explains the failure on stdout or stderr; show both, without PowerShell's error-record dressing.
        $err = (Get-Content $errFile | ForEach-Object { "$_" } | Where-Object { $_ -notmatch '^\s*(At |\+ |CategoryInfo|FullyQualifiedErrorId)' }) -join "`n"
        Remove-Item $errFile -ErrorAction SilentlyContinue
        throw "wrangler $($a -join ' ') failed (exit $code):`n$(($out -join "`n").Trim())`n$($err.Trim())"
    }
    Remove-Item $errFile -ErrorAction SilentlyContinue
    return $out
}

function Sql([string]$s) { "'" + $s.Replace("'", "''") + "'" }

function D1([string]$sql) {
    $out = Wrangler @('d1', 'execute', 'btucu-unlock', $where, '--json', '--command', $sql)
    $r = ($out -join "`n") | ConvertFrom-Json
    if ($r -is [array]) { $r = $r[0] }
    return $r.results
}

function Hash([string]$code) {
    $sha = [Security.Cryptography.SHA256]::Create()
    -join ($sha.ComputeHash([Text.Encoding]::ASCII.GetBytes($code)) | ForEach-Object { $_.ToString('x2') })
}

function NeedArg([string]$what) { if (-not $Arg) { throw "$Command needs $what" } }

# A Steam account as the server stores it: SHA-256 of "btucu-steam|" + the 64-bit id.
function SteamHash([string]$id) {
    if ($id -notmatch '^7656\d{13}$') { throw "a Steam id is the 17-digit number starting 7656 (steamid.io shows it as steamID64)" }
    Hash "btucu-steam|$id"
}

$now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
switch ($Command) {
    'init' {
        Wrangler @('d1', 'execute', 'btucu-unlock', $where, '--file', 'schema.sql') | Out-Null
        'tables ready'
    }
    'new-code' {
        NeedArg 'a name'
        # 20 characters from 32 that can't be confused (no I, O, 0, 1): 100 random bits.
        $alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789'
        $bytes = New-Object byte[] 20
        [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
        $code = -join ($bytes | ForEach-Object { $alphabet[$_ % 32] })
        D1 "INSERT INTO codes (hash, name, created) VALUES ($(Sql (Hash $code)), $(Sql $Arg), $now)" | Out-Null
        $shown = $code.Substring(0, 5) + '-' + $code.Substring(5, 5) + '-' + $code.Substring(10, 5) + '-' + $code.Substring(15, 5)
        if (-not $Local) {
            New-Item -ItemType Directory -Force $Keys | Out-Null
            $file = Join-Path $Keys 'access-codes.tsv'
            if (-not (Test-Path $file)) { [IO.File]::WriteAllText($file, "name`tcode`tcreated (UTC)`r`n") }
            [IO.File]::AppendAllText($file, "$Arg`t$shown`t$([DateTime]::UtcNow.ToString('yyyy-MM-dd HH:mm'))`r`n")
        }
        "$Arg`t$shown"
    }
    'revoke' { NeedArg 'a name'; D1 "UPDATE codes SET revoked = 1 WHERE name = $(Sql $Arg)" | Out-Null; D1 "SELECT name, revoked FROM codes WHERE name = $(Sql $Arg)" | Format-Table }
    'restore' { NeedArg 'a name'; D1 "UPDATE codes SET revoked = 0 WHERE name = $(Sql $Arg)" | Out-Null; D1 "SELECT name, revoked FROM codes WHERE name = $(Sql $Arg)" | Format-Table }
    'codes' {
        D1 "SELECT c.name, datetime(c.created, 'unixepoch') AS created, c.revoked, (SELECT COUNT(*) FROM unlocks u WHERE u.code_hash = c.hash AND u.ok = 1) AS unlocks, (SELECT datetime(MAX(u.at), 'unixepoch') FROM unlocks u WHERE u.code_hash = c.hash AND u.ok = 1) AS last FROM codes c ORDER BY c.created" | Format-Table
    }
    'log' {
        $filter = if ($Arg) { "WHERE c.name = $(Sql $Arg)" } else { '' }
        D1 "SELECT datetime(u.at, 'unixepoch') AS at, COALESCE(c.name, CASE WHEN u.code_hash LIKE 'steam:%' THEN 'steam ' || substr(u.code_hash, 7, 8) ELSE '(unknown code)' END) AS name, u.release, u.ok FROM unlocks u LEFT JOIN codes c ON c.hash = u.code_hash $filter ORDER BY u.at DESC LIMIT 50" | Format-Table
    }
    'retire' { NeedArg 'a release id'; D1 "INSERT OR REPLACE INTO retired (release, at) VALUES ($(Sql $Arg.ToLowerInvariant()), $now)" | Out-Null; "retired $Arg" }
    'unretire' { NeedArg 'a release id'; D1 "DELETE FROM retired WHERE release = $(Sql $Arg.ToLowerInvariant())" | Out-Null; "unretired $Arg" }
    'close' {
        D1 "INSERT OR REPLACE INTO settings (key, value) VALUES ('open', 'false')" | Out-Null
        if ($Arg) { D1 "INSERT OR REPLACE INTO settings (key, value) VALUES ('closed_message', $(Sql $Arg))" | Out-Null }
        else { D1 "DELETE FROM settings WHERE key = 'closed_message'" | Out-Null }
        'closed'
    }
    'open' { D1 "INSERT OR REPLACE INTO settings (key, value) VALUES ('open', 'true')" | Out-Null; 'open' }
    'steam' {
        $week = $now - 7 * 86400
        D1 "SELECT (SELECT COUNT(*) FROM steam) AS accounts, (SELECT COUNT(*) FROM steam WHERE borrowed = 1) AS borrowed, (SELECT COUNT(*) FROM steam WHERE blocked = 1) AS blocked, (SELECT COUNT(*) FROM unlocks WHERE code_hash LIKE 'steam:%' AND ok = 1 AND at > $week) AS unlocks_7d, (SELECT COUNT(*) FROM unlocks WHERE code_hash LIKE 'steam:%' AND ok = 0 AND at > $week) AS refused_7d" | Format-Table
    }
    'steam-block' { NeedArg 'a Steam id'; $h = SteamHash $Arg; D1 "INSERT INTO steam (hash, first, blocked) VALUES ($(Sql $h), $now, 1) ON CONFLICT(hash) DO UPDATE SET blocked = 1" | Out-Null; "blocked steam $($h.Substring(0, 8))" }
    'steam-unblock' { NeedArg 'a Steam id'; $h = SteamHash $Arg; D1 "UPDATE steam SET blocked = 0 WHERE hash = $(Sql $h)" | Out-Null; "unblocked steam $($h.Substring(0, 8))" }
    'steam-off' {
        D1 "INSERT OR REPLACE INTO settings (key, value) VALUES ('steam', 'off')" | Out-Null
        if ($Arg) { D1 "INSERT OR REPLACE INTO settings (key, value) VALUES ('steam_message', $(Sql $Arg))" | Out-Null }
        else { D1 "DELETE FROM settings WHERE key = 'steam_message'" | Out-Null }
        'steam route off'
    }
    'steam-on' { D1 "DELETE FROM settings WHERE key IN ('steam', 'steam_message')" | Out-Null; 'steam route on' }
    'family' {
        if ($Arg -notin 'allow', 'deny') { throw 'family needs allow or deny' }
        D1 "INSERT OR REPLACE INTO settings (key, value) VALUES ('family_sharing', $(Sql $Arg))" | Out-Null
        "family sharing: $Arg"
    }
    'status' { D1 "SELECT key, value FROM settings" | Format-Table; D1 "SELECT release, datetime(at, 'unixepoch') AS retired FROM retired" | Format-Table }
}
