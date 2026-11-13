# BattleTech UCU installer

This folder is the source of `BattleTechUCUSetup.exe`, the installer for BattleTech: Unofficial Community Update. It also
holds the tools that pack and publish a release, and the unlock server. You need your own copy of BATTLETECH to install.
The installer never changes that copy.

## What the installer does

1. **Finds your BATTLETECH install** from Steam's library files, or uses a folder you choose.
2. **Confirms you own the game.** It asks your running Steam client for an ownership ticket for BATTLETECH (app 637090).
   It sends that ticket to the project's unlock server, which asks Valve to confirm it. An access code can be used
   instead. The server stores no IP addresses (`keyserver/`, `docs/keyserver-design.md`).
3. **Downloads the release** from this repository's companion data repository, InternetDadCo/BattleTech-UCU-data.
   Only the files your install doesn't already have are downloaded.
4. **Installs into a separate folder.** It does two things:
   - copies files that are identical to the original game from your own install;
   - decrypts the rest from the package.

   It checks every file's hash as it writes it.

Nothing is written outside the install folder, apart from an accepted access code, which is kept in
`%APPDATA%\BattleTechUCU\access.txt`. The installer adds no registry entries, shortcuts or uninstaller. To uninstall,
delete the install folder.

## Checking what you downloaded and installed

- **The installer itself.** Each release's installer is built from this folder by the
  [Installer workflow](../.github/workflows/installer.yml), and GitHub records an attestation for it. To check a
  downloaded copy:
  ```
  gh attestation verify BattleTechUCUSetup.exe -R InternetDadCo/BattleTech-UCU
  ```
  The build is reproducible: with .NET SDK 10.0.403, `build.ps1` gives the same bytes as the workflow for the same commit.
- **What a release installs.** Each release publishes `ucu-manifest.tsv`, and its SHA-256 is in the release notes. It
  lists every file the release installs, with its size, SHA-512 and origin:
  - `retail`: identical to a file in your own BATTLETECH install, and copied from it;
  - `unity`: identical to a stock file from the Unity Editor version that built the game;
  - `built`: an executable this project builds;
  - `data`: anything else.

  The installer refuses a package that doesn't match its manifest.
- **An install on your disk.** Use *Verify install* in the installer window, `BattleTechUCUSetup.exe --verify <install
  folder>`, or, with nothing but PowerShell:
  ```
  powershell -ExecutionPolicy Bypass -File verify.ps1 -Install <install folder> [-Manifest <file or URL>]
  ```
  Installed files aren't encrypted, so any file can be inspected directly.

## Building

You need Windows and the .NET SDK 10.0.403. The exes target the .NET Framework that ships with Windows, so players need no
runtime.

```
powershell -ExecutionPolicy Bypass -File build.ps1
```

This writes `BattleTechUCUSetup.exe`, `ReleaseManager.exe` and `ReleasePack.exe` to `bin/`.

## Command line

```
BattleTechUCUSetup.exe --retail <BATTLETECH folder> --dest <install folder>   install without the window
BattleTechUCUSetup.exe --remote [github:<owner>/<repo>[@<tag>]]                 install or update from a release online
BattleTechUCUSetup.exe --verify <install folder>                                check an install against its manifest
```

Other options:

| Option | Effect |
|---|---|
| `--package <zip>` | Reads a downloaded package zip in place. |
| `--dry-run` | With `--remote`, shows what would be downloaded and written. |
| `--code <code>`, `--code-file <file>` | Gives an access code. |
| `--no-detect` | Opens the window without searching Steam's libraries. |

Exit codes:

| Code | Meaning |
|---|---|
| 0 | Installed, or verified. |
| 1 | Failed: a refused destination, a failed download, or not enough space. |
| 2 | BATTLETECH not found. |
| 3 | BATTLETECH or the unlock refused, or the package doesn't match its manifest. |
| 4 | A file failed its check. |
| 5 | Cancelled. |

## What's here

| Path | Contents |
|---|---|
| `src/` | The installer (`Setup.cs`, `SetupForm.cs`, `Installer.cs`, `Remote.cs`, `Unlock.cs`, `SteamTicket.cs`). |
| `src/GateCore.cs`, `src/Volumes.cs`, `src/PublicManifest.cs` | Shared code. |
| `src/Pack.cs`, `src/ReleaseManager.cs`, `src/GitHubReleases.cs` | The release tools. |
| `keyserver/` | The unlock server, a Cloudflare Worker, and its setup. |
| `docs/keyserver-design.md` | How unlocking works. |
| `test/` | The test kit: small synthetic releases and a local server that imitates GitHub. |
| `verify.ps1` | Checks an install against a manifest. |

Report installer problems in this repository's Issues.
