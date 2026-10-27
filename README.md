# Dave the Diver Multiplayer (DaveTheDiverMP)

Two-player online co-op for Dave the Diver: dive the Blue Hole together, see each other, share the fish, the
story and the sushi bar. Join a friend through Steam, or over LAN.

**Credits.** This mod was originally made by **[S1ii](https://github.com/S1ii)** as
[Coop-Divers](https://github.com/S1ii/Coop-Divers) (MIT). This fork is maintained by **Multiplayer Everything**
for the [Multiplayer Everything launcher](https://www.multiplayereverything.com), which installs it for you. The
fork adds Steam online play, fixes for the current game build (see [BUGS.md](BUGS.md)) and a two-copy lab test.
Fixes are offered back upstream.

Unofficial community mod. Not supported by MINTROCKET, NEXON or Valve.

## Install

The Multiplayer Everything launcher does all of this. By hand:

1. Download the BepInEx loader from the
   [`bepinex-6.0.0-be.790` release](https://github.com/multiplayereverything/Dave-The-Divers-Coop/releases/tag/bepinex-6.0.0-be.790)
   (the unmodified official BepInEx 6 IL2CPP build 790). Unzip it into the game folder, the one that holds
   `DaveTheDiver.exe`. Start the game once and wait for the title screen; the first start takes a minute or two.
2. Download `DaveTheDiverMP-<version>.zip` from the
   [latest release](https://github.com/multiplayereverything/Dave-The-Divers-Coop/releases) and unzip it into the
   same folder, so the DLL ends up at `BepInEx\plugins\DaveTheDiverMP\DaveTheDiverMP.dll`.
3. Both players need the same game build and the same mod version (the host refuses anything else).

To remove it, delete `BepInEx\plugins\DaveTheDiverMP`. Your normal saves are never touched: the shared campaign
uses manual save slot 7 (see "Saves" below).

## Play

On the title screen pick **Online**.

- **Host on Steam** opens a friends-only Steam lobby. Friends see "Join game" on your Steam profile, or you can
  press **Invite a friend**.
- Your friend opens **Online** and picks **Join: <your name>** (the list shows Steam friends who are hosting), or
  clicks Join game / accepts the invite in Steam.
- **LAN:** host with **Create lobby (LAN)** and give your friend your IP; they type it in and pick
  **Join by IP (LAN)** (UDP port 27777 by default).

When the friend is connected, they press **Ready**; the host presses **Start**. The host's current campaign is
copied to the friend, and both of you continue it together.

## How it works

| Part | What it is |
|---|---|
| Loader | BepInEx 6 bleeding edge, IL2CPP (`6.0.0-be.790`). Dave the Diver is Unity 6000.0.52f1 with IL2CPP; BepInEx generates the interop assemblies on first start and the mod is a .NET 6 plugin using HarmonyX patches on them. |
| Authority | The host owns the world. The client's actions that change shared state (hitting a fish, picking an item up, a dialogue choice, a sushi serve, buying something) are requests; the host applies them natively and sends the result. |
| Transport | **Steam** (online): a friends-only lobby (`ISteamMatchmaking`) tells friends who is hosting, and every datagram goes through `ISteamNetworkingMessages`, the connectionless half of SteamNetworkingSockets, relayed by Valve's Steam Datagram Relay. No IPs or ports, and the host only accepts sessions from members of its own lobby. The mod calls `steam_api64.dll` directly with the game's real app id (1868140), because the game's IL2CPP Steamworks.NET wrapper strips methods it doesn't use and Steam callbacks can't be registered from a plugin, so every async result is polled. **LAN** fallback: the same datagrams over UDP. There is no third-party relay server. |
| Protocol | Version 56, magic `DTMP`, 18-byte header, 59 packet types, max 1200 bytes per datagram. Its own reliability layer (acks, retries with backoff, a 1024-sequence receive window) runs on top of either transport, which carry unreliable datagrams only. |
| Connect | Hello / HelloAck with a build id: a hash of the game's build GUID, game version, Unity version and the mod's module id. Mismatches are rejected with a reason. One client per host. |
| Saves | The host packs its current game, photo and player data into a bundle and sends it in chunks; the client writes it into manual slot 7 and loads it. While connected, the client's own saves are blocked and its original profile is restored when it leaves. |
| Scenes | The host's scene changes (`ChangeSceneAsync`) are sent with a scene id, an epoch and the fish spawn seed. The client follows. Packets carry the scene id and epoch so nothing from an old scene applies in a new one. |
| Dives | Both players stand at the dive point and hold Start; the host starts the native dive when both are ready (2/2), and the client starts its own when it sees the host's transition. Leaving a dive also needs both living divers to confirm. A diver who dies spectates; when both die the dive ends. |
| World | Fish (a per-scene spawn seed so both sides spawn the same fish, then a host manifest, positions, HP and death), pickups, puzzle flags, bosses (3 families), boat decoration, ingredient storage and harpoon or bullet visuals. |
| Story and time | Host-driven "manager event" lanes (Story, Day, Time, Dialogue, Mission, Progression, Sushi), ordered by revision. Shared dialogue advances only when both players vote (continue, skip or a choice). |
| Sushi bar | The host runs customers, menu, wasabi and tables; the client sends pickup, serve, drink, clean and wasabi requests; end-of-night results are shared. |
| Puppets | Each player sends its position, rotation and sprites (20 Hz snapshots, 10 Hz visuals). The other side draws them as "DTMP Remote Diver" with a name label. |

Source layout: `src/Bootstrap` (plugin, update loop), `src/Networking` (UDP and Steam transports, protocol),
`src/Lobby` (title-screen room, save transfer), `src/Gameplay` (dives, travel, story, time, sushi),
`src/Replication` (world state), `src/Lab` (lab mode, used only with `-dtmp-lab`).

## Features and tests

Tested on 2026-10-27 with Dave the Diver Steam build 26209260 and two real game copies on one PC
(`tests\lab-suite.ps1`). Each check compares what the host logged with what the client logged, and has a
mutant run (one sync rule turned off) that must fail.

| Feature | What the lab checks | Mutant that must fail it | Result |
|---|---|---|---|
| Connect | Host and client connect (LAN, and the Steam path through SteamMock) | `connect` | ✅ |
| Saves | The client loads the host's campaign; both report the same fingerprint | `save` (corrupted transfer), `slot-reclaim` | ✅ |
| Player puppets | The host steps 2 m; the client draws the host's puppet there (within 0.3) | `puppet` | ✅ d=0.00 |
| Diving | Both hold Start on the boat; both enter the same dive scene | `dive-follow` | ✅ |
| Leaving a dive | Both confirm the exit; both are back on the boat | `dive-exit` | ✅ |
| Sea world | Both build the dive from the same sub-scenes | `layout` | ✅ e.g. A03_01_02, B06_02_02, C04_02_01 on both |
| Fish | Every client fish id is a host id; every fish near the divers is bound on the client | `layout` | ✅ |
| Fish kills and catches | The host's kill and the client's harpoon hit (a request to the host) show dead on both | `fish-request` | ✅ |
| Death | Both divers die; the dive ends and both are back on the boat | `party-wipe` | ✅ |
| Stalls | The client's game freezes for 12 s; the session survives | `short-timeout` | ✅ 0 disconnects |
| Story and dialogue | Shared dialogue ends on both; the client applied the host's last story event | `dialogue-spectator`, `story` | ✅ |
| Day and night | The client applied the host's last day/time/weather event | `day` | ✅ |
| Sushi bar | Both travel to the sushi bar (2/2 consent); the bar opens on both; sushi events applied | `travel`, `sushi` | ✅ |

Final run: 19/19 checks pass over LAN. Every mutant made its target check fail.

## Known issues

- **Online needs a real two-account test.** One Steam account can't P2P to itself, so the lab runs the Steam path
  (lobby, owner lookup, session accept, datagrams) through a stand-in (`SteamMock`) between two copies, plus LAN.
  Releases stay pre-releases until two Steam accounts have played together.
- Diver HP, oxygen and weapons are not synced: each player runs their own diver. Bosses only sync for three
  families, and clients can't damage bosses yet.
- The sushi bar, missions and time-stop fixes listed as open in [BUGS.md](BUGS.md) are not lab-tested.
- Only one client per host.

## Build from source

The game and BepInEx stay outside the repository. The build reads the game's BepInEx `core` and `interop`
folders from `GameInstallPath`:

```powershell
dotnet build .\DaveTheDiverMP.csproj -c Release -p:GameInstallPath="C:\Program Files (x86)\Steam\steamapps\common\Dave the Diver"
.\build\package.ps1 -Configuration Release
```

`package.ps1` writes `artifacts\DaveTheDiverMP-<version>.zip` (the plugin DLL and the license) with a SHA-256 file.

## Lab tests

```powershell
.\scripts\make-lab.ps1             # private\lab-host and private\lab-client: links to your install, own BepInEx
.\tests\lab-suite.ps1               # LAN: connect, save, puppet, dive, exit, story, day, fish, death
.\tests\lab-suite.ps1 -Steam        # same through the Steam code path (SteamMock)
.\tests\lab-suite.ps1 -Chaos        # 120 ms lag and 5% loss both ways (tests\chaos-proxy.py)
.\tests\lab-suite.ps1 -Mutant party-wipe   # must FAIL "death"
.\tests\mutation-suite.ps1          # every mutant; each must FAIL the check it targets
```

Lab copies start minimized as normal windows (restored small at the right edge of the screen), at 30 fps and
lowest quality, with BelowNormal priority. They use their own save folder (`lab-saves`), never your saves.

## License

MIT, see [LICENSE](LICENSE). Original work copyright S1ii; changes in this fork copyright Multiplayer Everything,
under the same license. Dave the Diver, its assets, BepInEx and the generated interop assemblies are not part of
this repository.
