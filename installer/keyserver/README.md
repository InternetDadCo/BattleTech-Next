# Unlock server

A Cloudflare Worker that unlocks packages made with an unlock server. It unlocks a release two ways:

- **Steam (the normal route).** The installer sends a ticket that Steam issued for BATTLETECH. The Worker asks Valve's
  Web API to confirm it. Steam only issues such a ticket to an account that owns the game, and no profile is read, so
  privacy settings don't matter.
- **Access code (the manual route).** For anyone Steam can't confirm: the Worker checks the code is valid and not revoked.

Either way it answers with that release's share. Design and reasons: `../docs/keyserver-design.md`.

What's here: `src/worker.js` (the Worker), `schema.sql` (its D1 database), `wrangler.toml` (its settings, no secrets),
`admin.ps1` (codes, the off switch, the record).

## Where to run it

On this workstation, programs under `tools/` can't write, and wrangler's local runtime fails there. `build.ps1 -Deploy
F:\Unity-Tools\ReleaseManager` copies this folder to `F:\Unity-Tools\ReleaseManager\keyserver`; run everything from
there. It keeps an existing `wrangler.toml` there, because that copy holds your database id. If `wrangler.toml` here
changes, copy the change over by hand.

## First setup (once)

In `F:\Unity-Tools\ReleaseManager\keyserver`, in PowerShell:

1. Install Node.js (LTS) if `node --version` fails.

2. Install wrangler, at the version `package.json` pins, then log in. The login opens Cloudflare in your browser.

   ```powershell
   npm ci
   npx wrangler login
   ```

3. Make the database, then copy the `database_id` it prints into `wrangler.toml` in this folder.

   ```powershell
   npx wrangler d1 create btucu-unlock
   ```

4. Create the tables.

   ```powershell
   .\admin.ps1 init
   ```

5. Make the master secret. In the Release Manager:
   - Set *Master secret file* to `F:\Unity-Tools\ReleaseManager\keys\master-secret.txt` and press *Create secret*.
   - Back it up with the content key. If it's lost, nobody can install releases packed with it.

6. Give the Worker the same secret. Paste the file's contents when asked; the Worker keeps it encrypted.

   ```powershell
   npx wrangler secret put MASTER_SECRET
   ```

6a. Give the Worker your Steam Web API key (`F:\Unity-Tools\ReleaseManager\keys\steam-webapi-key.txt`, from
   steamcommunity.com/dev/apikey). It's what the Worker asks Valve with. Without it, only access codes work.

   ```powershell
   Get-Content ..\keys\steam-webapi-key.txt | npx wrangler secret put STEAM_WEBAPI_KEY
   ```

7. Deploy. Note the `https://btucu-unlock.<your-subdomain>.workers.dev` address it prints.

   ```powershell
   npx wrangler deploy
   ```

8. Put that address in the Release Manager's *Unlock server URL*.

9. Make the test code the Release Manager checks the server with before every push, and save it beside the master secret:

   ```powershell
   .\admin.ps1 new-code release-manager-test
   ```

   Copy the printed code into `F:\Unity-Tools\ReleaseManager\keys\test-code.txt`.

From then on, packages from the Release Manager need the unlock server: Steam owners install with no code. To pack one
anyone with the BATTLETECH files can install, clear both unlock fields.

**Adding the Steam route to a server set up before it (2026-11-13):** step 6a, then `.\admin.ps1 init` (adds the `steam`
table; existing tables are kept), then `npx wrangler deploy`.

## Day to day

| Task | Command |
|---|---|
| Give someone access | `.\admin.ps1 new-code <name>` |
| Cut someone off | `.\admin.ps1 revoke <name>` |
| Undo that | `.\admin.ps1 restore <name>` |
| Who has codes, how often they're used | `.\admin.ps1 codes` |
| Recent unlocks | `.\admin.ps1 log`, or `.\admin.ps1 log <name>` |
| Withdraw a release | `.\admin.ps1 retire <release id>` |
| Undo that | `.\admin.ps1 unretire <release id>` |
| Stop all installs and updates | `.\admin.ps1 close "Message players see"` |
| Allow them again | `.\admin.ps1 open` |
| What's closed or retired | `.\admin.ps1 status` |
| Steam route: accounts, borrowed, blocked, unlocks this week | `.\admin.ps1 steam` |
| Stop one Steam account | `.\admin.ps1 steam-block <steam id>` (17 digits, 7656...) |
| Undo that | `.\admin.ps1 steam-unblock <steam id>` |
| Switch the Steam route off (codes keep working) | `.\admin.ps1 steam-off "Message players see"` |
| Switch it back on | `.\admin.ps1 steam-on` |
| Refuse or allow Family Sharing borrowers | `.\admin.ps1 family deny`, `.\admin.ps1 family allow` (default allow) |

- **`new-code`** prints the code once, as `XXXXX-XXXXX-XXXXX-XXXXX`, and adds it to
  `F:\Unity-Tools\ReleaseManager\keys\access-codes.tsv`. The server keeps only a hash of it.
- **Players** enter the code in the installer, which remembers it for that Windows user. Dashes, spaces and case don't matter.
- **Release ids:** the Release Manager's log shows a package's release id when it packs one.
- **Changing the Worker:** after you change `src/worker.js`, run `npx wrangler deploy` again.

## What players see

| Situation | Message |
|---|---|
| Wrong install | "needs an unmodified Steam install" (the server isn't asked) |
| Steam isn't running | "Steam isn't running. Start Steam, sign in…" |
| Signed-in account doesn't own BATTLETECH | "Steam didn't confirm that this Steam account owns BATTLETECH…" |
| Borrowed copy, with `family deny` | "This copy of BATTLETECH is borrowed through Steam Family Sharing…" |
| Blocked Steam account | "This Steam account can't unlock releases…" |
| Steam route off | your message |
| Bad or revoked code | "This access code isn't valid, or it was revoked." |
| Retired release | "This release has been withdrawn." |
| Closed | your message |
| Server unreachable | "Couldn't reach the unlock server…" |

The installer writes nothing until the code is accepted. A game that's already installed keeps working: the server
gates installs and updates only, never play.

## Limits and data

**Limits:**
- 30 unlocks per code, or per Steam account, a day.
- 10 requests per IP address a minute. Cloudflare's rate limiter holds this count in memory; nothing is stored.
- Valve allows 100,000 Web API calls a day per key.

**Data:** D1 holds the code hashes and names, retired releases, settings, and each unlock attempt (code hash or Steam
account hash, release, time, accepted or not). A Steam account is stored as SHA-256 of `btucu-steam|<steam id>`, with
the time of its first unlock and whether it was borrowed: no Steam ID, name or profile data. (Anyone who has a Steam ID
can work out its hash, so this hides IDs from a casual look at the table, not from someone checking a known ID.) No IP
addresses are stored.

**Cost:** Cloudflare's free plan covers this use.

## Testing locally

`npx wrangler dev --local --port 8787` runs the Worker with a local database:
- Set the database up with `.\admin.ps1 init -Local`. Every admin command takes `-Local`.
- Put `MASTER_SECRET=<hex>` in `.dev.vars` (ignored by git). For the Steam route add `STEAM_WEBAPI_KEY=<key>` and
  `STEAM_APP_ID=480`: app 480 (Spacewar) is one every Steam account can run, so tests don't touch BATTLETECH (637090).
- Point an installer at it with `BattleTechUCUSetup.exe --unlock-url http://127.0.0.1:8787 --code <code>`, or with
  `--steam-app 480` and no code for the Steam route.
