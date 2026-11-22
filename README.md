# Can 'Em All Multiplayer

Online co-op for **[Can 'Em All](https://store.steampowered.com/app/5133900/)**: up to 8 players (or unlimited) in the same factory on the host's save, with animated player models, emotes and a game-style multiplayer menu.

Made with BepInEx 6 (IL2CPP) and LiteNetLib (UDP). Everyone who plays needs the mod, and everyone must have the **same mod version**.

**No port forwarding needed:** Steam friends join you straight from the menu or with Steam's **Join Game**, and friends without the game on Steam join with a 6-character **lobby code**. Direct IP + port forwarding is still there as an option.

All multiplayer menus and messages follow the game's language (English, Spanish, German, French, Italian, Russian, Chinese, Japanese, Korean).

---

## Install

**1. Install BepInEx** (once per PC)

Download **[BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.793](https://builds.bepinex.dev/projects/bepinex_be/793/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.793%2Bdbc82f0.zip)** and extract everything into the game folder
(Steam: right-click Can 'Em All > Manage > Browse local files), so `BepInEx`, `dotnet`, `winhttp.dll` and `doorstop_config.ini` sit next to `Can 'Em All.exe`.

Launch the game once and wait for the main menu. The first launch takes a minute or two while BepInEx sets itself up.
Then close the game.

**2. Install the mod**

Drag the **`BepInEx`** folder from [`Install/`](Install) (or from the zip on the [Releases](../../releases) page) into the game folder and let it merge.
You should end up with:

```
Can 'Em All/
├── Can 'Em All.exe
└── BepInEx/
    └── plugins/
        ├── CanEmAllMP.dll          (the multiplayer mod)
        ├── CanEmAllMPModels.dll    (player models, emotes, weapon visuals)
        └── canemall_mp             (the player model and animations)
```

**Uninstall:** delete those three files (or the whole BepInEx setup: `BepInEx`, `dotnet`, `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`, `changelog.txt`).

---

## Playing

Everything happens from the **MULTIPLAYER** button on the main menu.

| Setting | |
|---|---|
| Name, Colour | How others see you (the colour also tints your miner) |
| Host save | Multiplayer has its own **3 save slots**, or use your normal single-player save |
| Connection | **Steam (friends)**, **Online (lobby code)** or **Direct IP (port forward)**. Picked at every launch: Steam if it works (Steam running and you own the game), otherwise Online. Direct IP is kept if you chose it |
| Friend | Steam: which friend to join (friends hosting right now) |
| Lobby code | Online: the host's code. Direct: the host's address and UDP port (default 7777) |
| Friendly fire | Red = off, green = on (the host decides) |
| Max players | 2 - 8 or Unlimited (including the host) |

- **Host:** open the **HOST** tab, pick your options and press **HOST LOBBY** (bottom right), then **START GAME** in the lobby when ready. People can still join after you've started.
  - **Steam:** your Steam friends see you in their JOIN tab, and Steam shows **Join Game** on your profile / in their friends list (it even starts their game and joins you).
  - **Online:** the lobby shows your **lobby code** (also copied to your clipboard); send it to your friends.
- **Join:** open the **JOIN** tab. Steam: pick the friend who's hosting. Online: paste the host's lobby code. Press **JOIN LOBBY** (bottom right) and wait in the lobby. The host's save is used, so joiners don't need any progress of their own and their own save is never touched.
- **CONTINUE / NEW GAME** stay plain single player.

In game:

| Key | |
|---|---|
| **Esc > MULTIPLAYER** | Players, kills / deaths / ping, your colour, friendly fire, **kick** (host) |
| **Tab** (hold) | Scoreboard |
| **Q** (hold) | Emote wheel (dances), with a third-person camera |
| **T** | Point |
| **F8** | Old multiplayer panel / status |
| **F9** | Weapon position tuning (with a test dummy) |

All keys can be changed in `BepInEx/config`.

### How connections work
- **Steam:** peer-to-peer through Steam's own networking (Valve's relay network), using the game's built-in Steam library. No servers of ours involved. Everyone needs the game on Steam and to be Steam friends; the host is advertised with Steam rich presence.
- **Online (lobby code):** the lobby server only hands out codes and introduces players. The game connects players **directly** (UDP hole punching) whenever possible, which has the lowest latency. If a network blocks that, it falls back to **Cloudflare TURN** (UDP, or TCP/TLS on port 443). The lobby server's own WebSocket relay is turned off by default (`RELAY_ENABLED` in `backend/src/index.js`).
- The lobby and the ping column show which path you got. `BepInEx/config` > `[Online] ForcePath` can force one for testing.

### Direct IP: port forwarding (host only)
1. Run `ipconfig` and note your PC's IPv4 address (e.g. 192.168.1.23).
2. In your router, forward **UDP** port 7777 (or your chosen port) to that address.
3. Give friends your public IP (search "what is my ip").
4. Allow the game through Windows Firewall when asked.

Can't port forward (some ISPs use CGNAT)? Use a VPN LAN tool such as Tailscale, ZeroTier or Radmin VPN on every PC and join with the host's VPN IP.
Testing on one PC: host, then join `127.0.0.1` from a second copy of the game.

---

## Features

- **Shared world:** one wallet, the host's creatures (including burns, dents and broken-off parts), cans, the starting basket, machines, truck, skills, permanent upgrades, gambling, mail letters and day timer are synced. The host's save is the save.
- **Players:** animated miner model in each player's colour, name tags, weapons held in hand, gun / knife stances, attack animations, 3D weapon sounds, 5 dances and pointing.
- **Combat:** optional friendly fire with knockback, hit sound and hit crosshair, vacuums pull players and bodies.
- **Death:** while a teammate is alive you spectate them instead of ending the day; your ragdoll stays behind for everyone.
- **Menus:** multiplayer screen, lobby, pause-menu player list with kick, Tab scoreboard, all in the game's own style. Shared cursors on the end-of-day screens, with a marker on the tab each player is on.
- **Stats:** kills, deaths, money gambled and won per player, saved with the save file.
- **Saves:** 3 multiplayer slots, automatic backups while hosting, and clear / copy / restore in the SAVES tab.
- **Languages:** every mod text in the game's 9 languages, switching with the game's language setting.
- **Fixes:** stuck creatures behind the truck are rescued, the day-end elevators wait for everyone.

---

## Troubleshooting

- **"Mod version mismatch"**: everyone needs the same release.
- **"Couldn't reach the host"** (Direct IP): wrong IP or port, port not forwarded (must be UDP), or firewall.
- **"No lobby with that code"**: check the code, or the host stopped hosting.
- **"Can't reach the lobby server"**: use Steam or Direct IP, or check your internet / firewall.
- **"Steam isn't running"**: start the game from Steam (friends without the game on Steam: use Online).
- **Friend not in the Steam list**: both on Connection: Steam, Steam friends, same mod version; press REFRESH.
- **"Couldn't connect through Steam"**: try again, or use Online (lobby code).
- **"Session is full"**: the host's max players is reached.
- **After a game update** the mod may need an update too; BepInEx rebuilds its files on the first launch after an update (that launch is slow).
- Logs: `BepInEx/LogOutput.log` in the game folder.

---

## Building from source

`source/CanEmAllMP` is the main mod, `source/CanEmAllMPModels` the model add-on (it references `CanEmAllMP.dll`), `source/unity-editor` the Unity editor scripts that build the `canemall_mp` asset bundle (animator setup + bundle export).

Both mods are plain C# (`langversion 10`, `-unsafe`) compiled against the game's BepInEx interop assemblies (`BepInEx/interop/*.dll`, `BepInEx/core/*.dll`, `dotnet/*.dll` from your game folder) plus the [LiteNetLib](https://github.com/RevenantX/LiteNetLib) sources. Embedded resources:
`CanEmAllMP`: `res/cursor0-7.bin` and `res/cursorS0-7.bin` as `cursorN` / `cursorSN`, `res/hitmarker.pcm` (16-bit mono 44.1 kHz) as `hitmarker`, `res/hitcross.bin` as `hitcross`, `res/loc.tsv` (translations, generated from `res/loc_src.py`) as `loc`; `CanEmAllMPModels`: `res/headmask.bin` as `headmask`.
`source/build.sh` is the build script used during development (reference list in a `refs.txt`).

`backend/` is the lobby server (a free Cloudflare Worker + Durable Object). To run your own, deploy it and set `[Online] LobbyServer` in the config.

---

## License

Use it, modify it, and share your own versions or forks **with credit**. Don't re-upload it unchanged and don't sell it. See [LICENSE](LICENSE) and [third-party notices](THIRD-PARTY-NOTICES.md).
Unofficial fan mod, not affiliated with the developers of Can 'Em All.
