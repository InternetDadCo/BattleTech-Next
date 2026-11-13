# Changelog

## 2.0.0-1B

Changes in version **2.0.0-1B** of the BattleTech: Unofficial Community Update (UCU), its first Beta build, from the final Alpha, 2.0.0-10A (see below for the complete list of changes between retail and the Alpha line).

- **Multiplayer text** goes through the game's localization, so it can be translated. English reads the same as before.
- **Fallback shaders are logged.** A short while after each scene loads, `Player.log` names any object drawing with a missing
  or unsupported shader. Include it when you report something drawn wrong.
- **Chat and Settings.** The co-op chat sits below an open Settings menu or the host's Multiplayer panel, so its title no
  longer covers the KEYBINDINGS tab on wide screens or a guest's permission boxes.
- **Game version for mods.** The game reports retail's version, 1.9.1, again, so a mod that names 1.9 or 1.9.1 as its
  game version loads as it does on retail, and retail no longer reads new saves as from a newer build. The port's own
  version, 2.0.0, is in `VersionInfo.PortVersion`; the main menu and multiplayer use it. Saves from earlier port builds
  still load in the port.
- **Crew conversations in co-op.** Guests now see the host's crew conversations and story cinematics, replayed on their
  own screen at their own pace. A guest with the Decisions permission can pick replies (as can the host). A guest busy elsewhere sees the
  scene from the start once free, then returns to where it was. Each player skips a cinematic alone; crew chats show
  the line only.
- **New crew in co-op.** When the host's story brings in a new crew member, guests now see them in the Argo's rooms
  too, and crew conversations no longer point the camera at someone who isn't there.
- **Depth-of-field log warning.** Conversations no longer fill the log with an `R8_SRGB` warning every frame. The
  blur looks the same.
- **Paradox analytics** reports name the game as UCU and its version,
  for example `UCU-2.0.0-1B`, so they can't be mistaken for retail's.
  The game sends only the reports Paradox's server accepts, the session start and the hardware summary; Paradox was rejecting everything else in the retail build, so we just don't send it.
- **Title screen.** Added (Unofficial Community Update) to the version display.

## 2.0.0-10A

What versions **2.0.0-1A** through **2.0.0-10A** of BattleTech UCU added and changed from retail *BATTLETECH* 1.9.1 (build 686R).

## New

### Campaign multiplayer

All of Campaign or Career mode can be shared with up to 3 other players. The host owns the game (Argo) state and clients receive save
snapshots, while combat keeps HBS's deterministic command model.

- **Sessions.** Player cap, password challenge, compatibility check, host approval, kick and ban. Permissions, bans and Always allow are kept per campaign, outside the save. Direct connect via IP can be probed and joined. A kicked or banned client stops reconnecting.
- **Joining.** A client  joins a running campaign and receives a snapshot, then stays in step through new snapshots at
  each time stop, with a day and funds tick between them.
- **DLC.** The host's DLC covers every guest. A guest who owns fewer DLCs, or none, can join and play the host's DLC
  content for the session, and is back to their own DLCs on leaving.
- **Argo.** Every client sees the host's Argo. A client can act only on the screens the host permits.
- **Screens.** MULTIPLAYER in the main menu's Career and Campaign flyouts opens Join Career or Join Campaign, a mode of the Skirmish browser
with skirmish-only settings pruned. MULTIPLAYER in the Argo's options opens a panel where the host opens the campaign,
  answers requests, sets seven permissions per player, and kicks or bans; a client sees it read-only. A player with no profile picks a
  callsign first. The Argo shows a Host Multiplayer or Guest Multiplayer tag under the career score (under the company
  name outside a career); the host's tooltip lists connected guests and a guest's lists their grants.
- **Co-op combat.** Per-unit control, host-ordered commands, AI on the host only, and a client that owns no unit can watch.
  Another player's portraits are dimmed and carry their name.
- **Contracts.** A host takes joined clients into a real contract. A client with no slot observes, a client that drops leaves
  its units with the host, and the host alone applies rewards before one snapshot returns everyone to the Argo. Joins are refused
  while a contract runs.
- **Mission setup and chat.** The host assigns each lance slot to a player on the retail lance screen. Each player readies,
  and Deploy stays off, with the reason shown, until all have. A changed 'Mech, pilot or owner takes back that player's ready. A
  client sees the contract, lance and owners before launch, then the host's results in an after-action view. Every player can chat
  on the Argo, in mission setup and in combat. Team-wide Reserve and Done-with-all are hidden in co-op. The chat can be moved and
  resized, and no longer blocks clicks on the screen behind it.
- **Rejoin.** A client that drops mid-mission can come back and finish the contract. Only players on the roster from contract
  accept are let back in, and their slots and units are restored.
- **Resync.** A peer whose combat state drifts from the host's is resynced from the host, up to three times per contract,
  before falling back to retail's desync. A host waiting on a rejoiner always gives up in the end and takes back the slots.
- **Save Snapshots.** Argo snapshots wait until a contract ends, and are compressed (about 42% smaller).
- **Stray shots.** Stray shots stay on in co-op, and the host now resolves every missed shot, so peers can't disagree about a
  secondary hit. Single player and Skirmish keep retail's path.
- **Mid-battle saves.** The host can save during a co-op mission and reload it later. The session reopens, and players from the
  roster rejoin into their slots; an absent player's slots go to the host after 180 seconds. With no co-op record the save loads as
  single player. The save format is unchanged.
- **Store for guests.** A client with the Store grant buys and sells. The host re-checks stock, price, funds and travel state,
  and refuses with a plain reason; of two buyers racing for the last item, one gets it and the other sees "Sold out".
- **Mech Bay for guests.** A client with the Mech Bay grant repairs, scraps, readies and stores 'Mechs, swaps bays and cancels
  work orders. The host builds every work order itself and refuses plainly. Clients can buy whole 'Mechs and pick where they go.
- **Mech Lab in co-op.** Opening a 'Mech in the Mech Lab locks it to that player. Anyone else, and anyone without the Mech Bay
  grant, sees the lab read-only with the holder's name. Parts taken from storage are claimed at once, so other labs see the lower
  count and claimed parts can't be sold. A client with the Mech Bay grant refits in their own lab; the host validates each
  refit and applies it the same way as in single player. Time and contract launch wait until every lab is closed.
- **MechWarriors for guests.** A client with the MechWarriors grant hires, dismisses, levels up and customizes pilots. The
  host checks each request against what the client saw (the applicant still listed at that price, the pilot's experience and
  skills) and refuses with a plain reason, such as "Someone else hired that MechWarrior." The commander can't be dismissed.
- **Engineering for guests.** A client with the Engineering grant buys an Argo upgrade and cancels the one in progress. The
  host checks the upgrade, its required modules, the cost shown and the funds, and the upgrade completes on the host as days pass.
- **Travel and time for guests.** A client with the Starmap grant sets a course, stops navigation, starts and pauses the
  host's clock, and accepts or aborts a flashpoint. The host plans the route itself and checks the cost and days shown.
- **Contracts and events for guests.** A client with the Contracts grant accepts a contract and launches an arrived travel
  contract; the host checks the terms and opens mission setup. Event popups show on every screen, and a client with Full Control
  answers them.
- **Permissions change live.** When the host changes a guest's permissions, the guest's screens follow at once. Anything
  the guest was confirming without the permission any more is cancelled, with one notice. A button the guest isn't permitted is
  grey from the first frame. Alliances stay the host's, and guests see what the host took as salvage.
- **Translatable text.** The multiplayer screens' text goes through the game's translation tables, as retail's own
  text does, and a guest shows the host's messages in the guest's language. Only the English text exists so far.

### Mod support

DLL mods run on stock ModTek through a small port bootstrap, on D3D11 and D3D12. We keep every existing member's name and signature so Harmony patches still land.

- Doorstop is v4.6.0. The copy ModTek ships crashes when Unity loads `d3d12.dll` by ordinal.
- The bootstrap applies three Mono 6.13 compatibility fixes and runs ModTek's assembly shim on `Assembly.LoadFile`.
- For mods built against Unity 2018, it writes a forwarded `CoreModule` copy under `Mods/.port` so `UnityEngine.Input` resolves.
- For Newtonsoft 13, it writes a copy with the three internals ModTek's merges call. Shipped DLLs stay stock.
- The game boots through retail's Main chain, which ModTek injects into. The embedded uGUI keeps retail's
  `ForceMeshUpdate(bool)`, `DeactivateInputField()` and 4-parameter `SetLayoutInputForAxis` as overloads.

### Diagnostics

- **Shader check.** Thirty seconds and two minutes after each scene loads, Player.log gets a `[ShaderFallbackCensus]`
  summary line, and one line for each object that draws with a missing or unsupported shader.

## Optimization

Each of the below optimizations passed a seeded equality check against the unpatched game. 
| Change | Measured |
|---|---|
| AI debug logs built only on request (new `aiLogCacheEnabled` setting, off by default) | Lower AI turn cost |
| `DataLoader.GetEntry` uses a dictionary | `LoadResource` 1,019/1,137 to 860/779 ms |
| `FromJSON` skips per-member reflection waste | 3,842 defs: 2,392/2,264 to 905/864 ms |
| Shader bundles stay pinned across `AutoUnloadAll` | Career load 29.1 to 17.8 s from the menu, 36 to 17.2 s in game |
| `RefreshTypedEntries` indexes addenda once | About 0.5 s per refresh |
| No scene-wide searches while no map is loaded; terrain tiles share one clip root | About 2.1 s per combat load |
| Each saved type name is looked up once | Save decode 16.4 to 8.1 s |
| Index for the tag lookup in the in-memory database | Tag SQL in Deploy 7.6 to 0.27 s |
| No camera scan while no camera exists | Camera scan in Deploy 7.8 to 0.02 s |
| Loads drop vsync, the frame rate target and low background priority, then restore them | Modded BTA career: load to the Argo 50.1 to 36.0 s, in-game reload 38.4 to 25.8 s, measured with the display unattended |
| Shader requests open at the first scene and load async; `VFX/Alpha` read once | Boot 9 s faster, combat load 3.6 s faster |

Together, the save decode, tag lookup and camera scan changes take a modded save from 79.8 to 62.6 s and an in-game reload
from 55.4 to 45.8 s.

## Retail bugs fixed

Multiplayer:

- Direct and Online Direct share a socket, and strategies that weren't selected swallowed messages.
- A networked mortar strike lost its target positions on the wire.
- Support weapons had a null uid, so a receiver could never match the weapon.
- A forwarded artillery activation ran again, firing a second strike on receiving peers.
- A deferred `ReadString` read a recycled message and corrupted the host's settings update.
- The host's one-shot Ping could arrive before the server port was bound, and a retry reported failure too early.
- One lost datagram ended a join. The handshake now retries.
- The next team could activate while a peer's order was still running, which deadlocked whole Skirmish matches.
- A client sending an empty password hash skipped the host's password check.
- The handshake timeout fired off the main thread, leaving a client on the Argo with no message.

Elsewhere:

- Concurrent Vexe reflection builders crashed natively. They now lock the shared emitter.
- A Steam read refused during a concurrent write reported success, handing the deserializer an empty buffer. Retail tears
  246 of 256 overlapped loads; the port delivers all 256.
- Every quit logged a CRITICAL ERROR from analytics.
- Each tree teardown leaked a culling group.
- `Shop.Purchase` lowered stock before checking funds, so a buy the company couldn't pay for still used up the item.
- A settings save made while another was still writing was refused, so the newer settings were lost. The newest one now waits
  its turn.
- Quitting logged "has no receiver" for every object without a quit handler.
- Closing a scene could create a new UI element manager while tearing down, leaving objects behind.
- Retail ships components and assets whose scripts were stripped, each logging a missing script. They're removed.
- Retail map data has two tree types with no prefab on a story map and antennas with no mesh on Boulder Field and The Mound,
  each logging an error on load. The trees are dropped and the antennas skipped quietly; nothing that drew changes.
- On a story map, the encounter's self-check counted buildings inside its own area twice and logged an error.
- Rebuilding a map's water edge left the old obstruction logic alive for a frame, so it was registered twice.
- Urban Warfare's opening conversation on New Jakobstad handed its music cue to the holo screen instead of the audio player, so
  the music never started and an error was logged. The music now plays, and the holo screen shows the same image as before.
- On Itrom's Restoration map, one artillery area was a copy of the other with the same six target point IDs, so each load
  logged twelve duplicate-ID errors. The copy's points have their own IDs now; both areas keep their six points.
- Quitting, or leaving combat, while a sound effect was still playing, such as a building collapse, logged a null-reference
  error from the audio shutdown. The sound's end callback no longer reaches for the game or the sound after they're gone.
- Quitting from combat could log a null-reference error from the fog of war, when its view was torn down before the
  objects that reveal it. Those objects now skip the view once it's gone.
- A joystick, throttle or pedal resting off-centre walked the menu selection, so an open dropdown showed a second highlight
  climbing from the hovered row to the top. Joystick axes no longer drive UI navigation.
- The main menu kept its intro-end handler after it closed, so an intro that ended later could run it on the closed
  menu's video player.
- **Encounter boundary.** Retail's boundary mask is all-opaque, so its distance field never spread. The fog now draws a gold
  dashed band.

## Changed from retail

- **Services.** Analytics, Paradox, Sift, AWS and ServiceStack callers are off. `S3Manager` is an offline shim with the same
  API, and the four AWSSDK DLLs are gone. Changing the callsign saves it locally instead of waiting on ComStar.
- **Version.** Retail 1.9.1 saves read as older, so an Ironman save is backed up on load. New saves carry 2.0.0-10A. Multiplayer no
  longer matches retail clients. The mod-loader cache re-keys once. The unused `VersionInfo.CURRENT_VERSION_NUMBER` still says 1.9.1.
- **Graphics API.** D3D11 is the default, as in retail, and D3D12 is supported, modded and unmodded. Graphics jobs are off.
- **Third-party code.** Newtonsoft 13, current DOTween, the Steamworks.NET package and SVG Importer 2.0. InControl, Lidgren,
  Harmony 1.2, NGettext, Ionic.Zip, Dapper and protobuf-net stay as retail binaries.
- **Audio and video.** Retail's Wwise 2016.2.2 and Bink 1.300n runtimes with retail banks and movies, with no transcoding.
- **Developer mode.** The main menu's SIM GAME and DEBUG buttons depend on `DebugBridge.testToolsEnabled`: on in the Editor and
  development builds, off in release.
- **Editor only.** Saves go to `EditorCloud` and achievements to a log, so the Editor can't touch a developer's Steam data.

## Required technical changes

These keep retail's behaviour where retail's code or data doesn't work as shipped on Unity 6.

### Code and compiler

- Replaces 2018 Unity API calls, with an exact `GetInstanceID` helper. `BTLight.CompareTo` keeps the real call because a
  ModTek injector patches it.
- Updates HBS's TextMeshPro and uGUI wrappers for current packages.
- Hand corrections to decompiler output, and Unity's API updater output applied as a reviewed patch.
- Drops a duplicate `AssemblyVersion`.
- Removes a `GetControlID` call that threw outside `OnGUI` and stopped the debug console loading its history.
- **Assemblies.** HBS code compiles into `Assembly-CSharp` and `Assembly-CSharp-firstpass`, the names retail's bundles reference.
  The firstpass tree is under `Plugins/HBS/`, `allowUnsafeCode` is on for Bink, and the language version is C# 12.

### Saves and serialization

- Maps type names so retail saves load.
- Skips a serializer candidate that throws `TypeLoadException`, because `TouchPhase` moved with no forwarder.
- Restores retail's unlimited JSON `MaxDepth`, which Newtonsoft 13 caps at 64.

### Textures and data

- Rounds DXT textures up to a multiple of 4, which Unity 6 requires, and a failed request no longer blocks the queue behind
  it (this had hung Campaign START).
- Applies the same limit to sprites loaded from disk: decoded as RGBA32, recompressed to DXT5 only when aligned.
- **Texture flags.** 4,827 textures use retail's sampling flags. 1,783 ripped textures had the alpha-is-transparency
  flag on, which painted window colour over the facade strips of urban office blocks; it is off, as built textures have none.
  2,664 textures were built uncompressed, which more than doubled the texture data; they now use the compressed format retail ships.
  619 more were built as BC7 where retail ships DXT1, DXT5 or Alpha8; they now use retail's format too.
  Six textures (the Leopard's hub and three rocks) were capped at half retail's resolution; they're full size again.
- **HDR textures.** 108 textures that retail stores as half-float or BC6H were extracted as 8-bit. They're restored at retail's
  format: the sky LUTs no longer draw contour bands on the loading-screen planet, lens flares no longer clip, and urban
  buildings collapse in smooth steps instead of jumps.
- **Encounter logic.** Stored inside the map scenes, as retail does. All 85 maps read back equal to the source.
- **Meshes.** Re-saved at version 12, which ends a warning on every load, and 491 wrong bone hashes restored. Geometry is unchanged.
- **Dangling assets.** The six attack direction arc meshes are staged, and all eight lunar terrain layers name real textures.
  Twelve biome assets named drop-pod effects and road materials the rip didn't have; they now name retail's, so the falling drop
  pod shows again. Five moods (Martian dust storm, jungle monsoon and allergy season, and two Restoration missions) named
  camera weather effects that weren't staged, so those moods played without their dust, rain or pollen; the effects are staged now.

### UI and scenes

- Guards `HBS_InputField.UpdateLabel` against re-entry, which overflowed the stack in character creation.
- Replaces a `foreach` that invalidated the in-world manager on the first `CombatHUD.Init`.
- `ElementManager` no longer throws when `UnityGameInstance` is already gone.
- `SVGImage` with a null mesh draws nothing instead of throwing.
- Keeps HBS's additions (`fillAmount`, `SVGCamera`) on SVG Importer 2.0, which is used through a pinned public fork. The fork
  also fixes `SVGImage` with no canvas, which drew blue boxes and green bands on the career HUD.
- **Animators.** `ArgoController` and the crew controllers have their `StateMachineBehaviour` lists restored, so travel ends and the
  crew vary their idles.
- **Star map.** The 21 VFX prefabs behind `StarPrototype` are staged, which ends an exception when a flashpoint is available.
- **Selectables.** Unity 6's default Selected colour replaced retail's highlight. Fixed in 14 prefabs.
- **Prefab references.** Retail references restored where they named nothing in the port.
- **uGUI package.** The game camera, the movement reticle and the quality settings list still named Unity 2018's UI DLL, which
  a player build can't find. They now name the uGUI package.
- **Script references.** 747 SVG documents and eight Raw Cliffsides dialog images named script GUIDs from the rip. They're
  rebound to the port's SVG Importer.
- **Ellipsis.** Unity 6's TextMeshPro looked the ellipsis up only in the bold or italic font slot, which BattleTech's fonts leave
  empty, so bold text cut off without one. It falls back to the regular weight, as retail did.
- **Credits.** Each slide was loaded three times, so the slideshow showed every slide three times at a third of retail's pace.
  It shows retail's 152 slides at retail's pace.
  The BATTLETECH, Urban Warfare and Heavy Metal title logos named missing Sprites, which threw four errors; they show retail's art.
- **Wide screens.** The main menu and popup canvases scale with Expand, as retail's do, so 21:9 and 32:9 screens keep the full UI.
- **Startup logos.** Retail's three splash logos play at startup again.
- **Shutdown.** `OnGameShutdown` is added after `UnityGameInstance`, as retail's `Main.unity` does.

### Networking and platform

- The external IP lookup uses HTTPS, because Unity 6 refuses plain HTTP.
- Two achievement call sites rewritten for the Steamworks.NET package.
- Wwise shuts down cleanly when the Editor quits.
- In the Editor, closing a map scene no longer calls `Destroy` outside Play: the mood's sky sphere and the post-processing stack are only destroyed in Play.
- On D3D12, Bink is handed the video's own texture, because the back buffer has no resource behind it.

### Rendering

- **Pipeline.** Built-in Render Pipeline with Linear colour space, retail's configuration, so HBS's renderer runs unchanged.
- **Shaders.** Unity 6 can't load 2018 shader bytecode, so HBS shaders are rebuilt as source and checked against retail's bytecode
  (`tools/shader-recovery/`). Those not yet recovered, including the mech shaders, are served from retail's `shaders`
  bundle through a binder.
- **Compute shaders.** Retail's bundle has no usable kernels on Unity 6, so an overlay bundle replaces it.
- **Variants.** Player builds keep only the keyword sets retail shipped, instead of 17x to 121x as many.
- **Shaders found by name.** SVG Importer and TextMeshPro shaders are Always Included. `Hidden/BT-VFX-Background`,
  `Hidden/BT-ExplosionFlare` and `UI/BT-ColorSwatch` are recovered, ending exceptions on artillery, strikes and Company Customization.
  `SaveShadowParams` asks `Shader.Find` for the right name.
- **Bundle-only materials.** A player build drops the data of materials whose shader exists only in a bundle, leaving metallic paint
  black and the Argo dark. Generated `RetailMaterialData` assets restore it.
- **Decals.** Terrain and road decal materials named a copy of HBS's decal shader with no instanced variant, and Unity 6
  dropped their draws without a log line. They now use the recovered shader, so retail's dust, snow and road markings draw.
- **Realtime GI.** Unity 6's Enlighten couldn't bake the maps at retail's settings, so each battle map's bake is rebuilt from
  retail's own layout: the terrain and the props retail baked, merged into retail's systems. Each prop's ripped pieces are welded
  back into one mesh and kept as a hidden GI proxy with its own material, so the lighting data stays near retail's size and the
  props give Enlighten their colour. The terrain does the same through retail's recovered FarPass shader. At load, each visible
  prop takes its proxy's place in the GI atlas, and its static-batched mesh carries the matching coordinates, so props draw from
  realtime GI as retail's do instead of from light probes. Only maps without live probes get the mood's ambient probe as a fill.
  The proxies sit on their own layer, which no camera draws, so they stay off the loading screen and out of shadows.

