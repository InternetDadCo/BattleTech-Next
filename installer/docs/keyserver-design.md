# Unlock server: design

Status: built 2026-11-13 and tested against a local Worker (Testing plan, below); deployed to Cloudflare the same day
(codes only, until the Steam route is deployed). Players who own BATTLETECH on Steam unlock through Steam with no code
(*Steam route*); the maintainer's own access codes (no itch keys) are the manual route. A package needs an access code when the Release Manager's two unlock fields are set;
with them empty it packs v2 as before. Packages up to 10A (vault v2) are unaffected. Setup and use: `../keyserver/README.md`.

## Goal

A release installs only after a server we run agrees. On top of today's retail check, the installer needs a
per-release secret that only the server hands out, and only to a valid, unrevoked access code. This gives:

- **Per-person access:** each player has their own code; revoking one affects no one else.
- **Off switch:** turning the server off stops every new install and update of every v3 release.
- **A record:** which code unlocked which release, and when.
- **Leaked packages are useless alone:** the package plus BATTLETECH isn't enough without a code.

It does not protect an installed game (files sit decrypted on disk), and it can't prove an installer is genuine:
the server checks the person (their code), never the program.

## Keys

Today (vault v2): `package key` = the reused content key. The vault holds one slot per accepted retail
fingerprint, each sealing the package key under that fingerprint. The manifest is sealed under the package key.
Blob names, keys and IVs come from the package key plus each file's plaintext SHA-256.

v3 adds a **release share**:

```
release id   = 16 random bytes, made at pack time, stored in the vault in clear
share        = HMAC-SHA256(master secret, "btucu-share|" + release id)
slot secret  = HKDF(fingerprint || share, salt = slot salt, info = "slot3")
manifest key = HKDF(package key, salt = share, info = "manifest3")
```

- **One master secret, no per-release upload.** The Worker and the Release Manager both hold the master secret;
  each release's share is computed from it, so packing a release needs no server step.
- **Updates still download only what changed.** The package key and the blob derivation are unchanged, so an
  unchanged file keeps its blob.
- **An old package key doesn't open a new release.** A player who dumps the package key from memory during one
  install can derive a blob's key only from the file's plaintext hash. Those hashes are in the manifest, and a new
  release's manifest needs that release's share. So a changed file stays locked; unchanged files they already have.
- **The master secret** lives at `F:\Unity-Tools\ReleaseManager\keys\master-secret.txt` (beside the content key,
  backed up the same way, never in the repository or a package) and as a Worker secret. If it leaks, make a new one:
  later releases use it, and earlier releases stay on the old one, or are retired.

## Vault v3

```
"BTU6GAT3"
release id (16 bytes)
unlock URL (length-prefixed UTF-8)     where to ask; the installer also has a compiled-in default
slots     (as v2, sealed with the v3 slot secret)
manifest  (as v2, sealed with the v3 manifest key)
```

The new magic makes older installers hand off to the release's own installer (the existing
NeedsNewerInstaller path), so an old installer never misreads a v3 vault.

## Server (Cloudflare Worker + D1)

`tools/release-gate/keyserver/`: `worker.js`, `wrangler.toml`, `schema.sql`, admin scripts, tests. No secrets in it.

**Endpoint:** `POST /v1/unlock` with JSON `{ "release": "<hex id>", "code": "<access code>", "installer": "<version>" }`.

| Result | Status | Body |
|---|---|---|
| unlocked | 200 | `{ "share": "<hex>" }` |
| unknown or revoked code | 403 | `{ "error": "code" }` |
| release retired | 410 | `{ "error": "release" }` |
| server switched off | 503 | `{ "error": "closed", "message": "..." }` |
| too many tries | 429 | `{ "error": "slow-down" }` |

**D1 tables:**

- `codes(hash, name, created, revoked)`. Holds a SHA-256 of each code, never the code itself, plus the name you gave it.
- `retired(release)`: releases the server refuses.
- `unlocks(code_hash, release, at, ok)`: the record. **No IP addresses are stored.**

**Settings:**

- Worker secret `MASTER_SECRET`.
- Variable `OPEN` (`true`/`false`), the off switch, with an optional `CLOSED_MESSAGE` shown to players.

**Limits:**

- Per code: 30 unlocks a day.
- Per IP: Cloudflare's rate-limiting binding, held in memory, not stored: 10 requests a minute (the binding counts
  over 10 or 60 seconds only).

Free tier (100k requests a day, D1 100k writes a day) is far above this use.

**Admin:** `keyserver/admin.ps1` runs `wrangler d1 execute` for:

- `new-code <name>`: prints the code once, and appends name plus code to `keys\access-codes.tsv` (offline; the D1 table has hashes only);
- `revoke <name>`
- `retire <release>`
- `log [<name>]`
- `close [<message>]` / `open`

**Codes:** 20 random base32 characters, shown as `XXXXX-XXXXX-XXXXX-XXXXX`. Dashes, spaces and letter case are ignored.

## Installer

- **When the vault is v3**, the window asks for the access code under the retail folder. It's remembered in
  `%APPDATA%\BattleTechUCU\access.txt` (that user only).
- **The unlock happens at Check**, so a bad code is reported before anything is written.
- **Headless:** `--code <code>`, or `--code-file <file>`.
- **Testing:** `--share-file <file>` unlocks with a share directly, with no server. The Release Manager uses it for its own test installs.
- **Messages:**
  - "This access code isn't valid or was revoked."
  - "This release has been withdrawn."
  - The server's closed message.
  - "Couldn't reach the unlock server (…). Check your internet connection and try again."
- **The share is never written to disk.** Every install or update asks again: one request each.
- **Itch website zip, loose folder and `--remote`** all read the same vault, so all three get this.

## Release Manager

- **New setting:** the master secret file, with a "Create" button if it's missing, in the same way as the content key.
- **Packing:**
  - **v3 by default.** A "No unlock server (v2)" tick box keeps the old format.
  - **The log** shows the release id.
  - **The test install** unlocks with `--share-file`, written to a temporary file and deleted afterwards.
- **Push:** before pushing a v3 release, check that the server answers `/v1/unlock` for that release id with a
  test code (`keys\test-code.txt`, a code named `release-manager-test`). Refuse to push if it doesn't.

## Not built: itch download keys as codes

The maintainer chose our own codes (2026-11-13). The Worker could later also accept an itch download key, checked with itch's
server-side API.

## Testing plan

1. **Worker:**
   - Run it with `wrangler dev` (local D1).
   - Test every status code, the limits, and revoke, retire and close.
2. **Installer against the local Worker:**
   - Install a synthetic v3 package.
   - Check that a bad code, a revoked code and a retired release are refused before anything is written.
   - Update one v3 release to the next, and check that only the changed blobs download.
3. **Older installers:** a v2 installer starting a v3 release hands off to the release's installer.
4. **Full package:** repack full-10 as v3 to a test folder and do a scripted install against the local Worker.
   The result must be file-equal to the v2 install.
5. **Live:** once the maintainer has deployed the Worker, one install from the test code against the live address.

## The maintainer's part

- Create the Cloudflare account.
- Install Node.js and `wrangler`, then `wrangler login`.
- Create the D1 database (`wrangler d1 create btucu-unlock`). Paste its id into `wrangler.toml`.
- Set the master secret: `wrangler secret put MASTER_SECRET`, pasting `keys\master-secret.txt`.
- Deploy: `wrangler deploy`. Note the `*.workers.dev` address; it becomes the installer's default unlock URL.
- Issue codes with `admin.ps1 new-code <name>` and send each one to its player.

Agents write and test the Worker locally. They don't create the account, log in to Cloudflare or handle the master secret.

## Decisions

1. **Codes:** our own (the maintainer, 2026-11-13).
2. **When a package needs a code:** when the master secret file and the unlock URL are both set in the Release Manager;
   both empty packs v2.
3. **Server down:** an existing install still starts the game. The server gates installing and updating only, never play.
4. **Steam ownership is the normal route; codes are the exception** (the maintainer, 2026-11-13): GitHub releases are public, and
   players can't be asked to make their Steam profiles public. See *Steam route*.
5. **Family Sharing borrowers:** allowed by default; `admin.ps1 family deny` refuses them.

## Steam route (2026-11-13)

**How the installer gets a ticket:**
- With no access code entered, it loads `BattleTech_Data\Plugins\steam_api64.dll` from the player's own BATTLETECH
  folder and starts Steam's API as app 637090.
- It asks the running Steam client for a Web API ticket bound to the identity `btucu-unlock`. Retail's DLL (about 2018)
  lacks that call, so it goes through the client's `SteamUser023` interface, vtable slot 14. A client without that
  interface gets the older session ticket instead.
- Ticket bytes arrive as callback 168. They are read with `Steam_BGetCallback` from steamclient64.dll.
- The ticket is valid only while Steam's API is up, so the installer keeps it up until the server has answered, then
  cancels the ticket and shuts the API down.

**How the server confirms it:**
- The Worker calls `api.steampowered.com/ISteamUserAuth/AuthenticateUserTicket/v1` with the regular Web API key
  `STEAM_WEBAPI_KEY`, `appid=637090` and the identity. It accepts only `result: "OK"`.
- The account's hash goes in the `steam` table. A difference between `steamid` and `ownersteamid` marks a borrowed copy.

**Why it shows ownership:**
- `SteamAPI_Init` fails for an account without a licence: tested with an unowned app, 2050650.
- Valve binds each ticket to its app and its identity: a ticket checked against another app gives error 102, and the
  wrong identity gives error 105.
- Valve's documentation says this call needs a publisher key and calls Web API tickets identity-only. Tested on
  2026-11-13, a regular key works on api.steampowered.com, and no profile is read. Evidence: gbrain
  `lessons/steam-ticket-ownership-without-publisher-key`.

**Limits:**
- Someone who controls their own Steam client could still subvert the client-side licence check. A ticket Valve
  confirms is still tied to a real account, which can be rate-limited and blocked.
- The licence check that is fully server-side is `CheckAppOwnership`. It needs Paradox's publisher key.

**Release Manager:** before a GitHub upload, it sends the live server a dummy ticket. The dummy must reach Valve and be
refused there, which shows the route is on and the key works. Before an itch.io push it only warns.

## Test record (2026-11-13)

All of these ran against `wrangler dev --local` (wrangler 4.161.0), from a copy of `keyserver/` outside `tools/`. Inside
`tools/`, wrangler's runtime can't create its folders on this workstation.

**Worker:**
- A good code got a share equal to the one computed locally from the master secret.
- An unknown code and a revoked code got 403; a malformed request 400; GET 405.

**Synthetic v3 package** (five files), packed with ReleasePack, installed headless:

| Case | Result |
|---|---|
| Short code | refused before the server was asked |
| Revoked code | refused |
| Good code | installed, file-equal to the player |
| Server unreachable | refused with the "couldn't reach" message |
| Wrong retail install | refused with no request to the server; its log unchanged |
| Retired release | "withdrawn" |
| Closed | the close message |
| `--share-file` | installed with no server |
| Wrong share | "doesn't fit this release" |

Nothing was written for any refused case.

**v2 compatibility:**
- The same player packed v2 installed as before.
- Its blob names equal the v3 package's, so updates still download only what changed.

**Release Manager `CheckServer`:**
- refuses with no test code, with a different master secret, and with no master secret file for a v3 package;
- passes for a good server;
- skips a v2 package.

**Installer window**, driven by UI Automation:
- The access code row shows for a v3 package.
- A revoked code shows the refusal.
- A good code shows "Access code accepted", enables Install, and is remembered.

**Steam route, against `wrangler dev` with `STEAM_APP_ID=480` (Spacewar), a real Steam ticket and Valve's live API:**
- An install with no code succeeded (1 account recorded, not borrowed).
- A blocked account was refused.
- `steam-off` showed its message.
- A forged ticket got 403 "Invalid parameter", and a malformed one got 400.
- The code route still installed.
- A BATTLETECH folder without steam_api64.dll gave a clear message.
- The window showed "Steam confirmed that you own BATTLETECH."
- `SteamRouteProblem` reported on, off and unreachable correctly.
- One leftover: Steam prints `pipes.cpp (733) … m_QueueCallbackMsg` at API shutdown. It appears only on the headless
  console and has no effect.

**Not run yet:**
- The deployed Worker on Cloudflare, including its rate limiter.
- The Steam route as app 637090. The lease was held; the first real unlock covers it.
- A Family Sharing borrower.
- A full-size repack. Agents don't cut packages, so the maintainer's first v3 pack and its test install are the full-size test.
