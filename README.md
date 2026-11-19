# Can 'Em All Multiplayer

Online co-op for **[Can 'Em All](https://store.steampowered.com/app/5133900/)**: up to 8 players (or unlimited) in the same factory on the host's save, with animated player models, emotes and a game-style multiplayer menu.

Made with BepInEx 6 (IL2CPP) and LiteNetLib (UDP). Everyone who plays needs the mod, and everyone must have the **same mod version**.

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
| Address / Port | Who to join / which UDP port to use (default 7777) |
| Friendly fire | Red = off, green = on (the host decides) |
| Max players | 2 - 8 or Unlimited (including the host) |

- **Host:** press **HOST GAME**. A lobby opens where friends can connect; press **START GAME** when ready. People can still join after you've started.
- **Join:** type the host's address, press **JOIN GAME** and wait in the lobby. The host's save is used, so joiners don't need any progress of their own and their own save is never touched.
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

### Port forwarding (host only)
1. Run `ipconfig` and note your PC's IPv4 address (e.g. 192.168.1.23).
2. In your router, forward **UDP** port 7777 (or your chosen port) to that address.
3. Give friends your public IP (search "what is my ip").
4. Allow the game through Windows Firewall when asked.

Can't port forward (some ISPs use CGNAT)? Use a VPN LAN tool such as Tailscale, ZeroTier or Radmin VPN on every PC and join with the host's VPN IP.
Testing on one PC: host, then join `127.0.0.1` from a second copy of the game.

---

## Features

- **Shared world:** one wallet, the host's creatures, cans, machines, truck, skills, permanent upgrades, gambling and day timer are synced. The host's save is the save.
- **Players:** animated miner model in each player's colour, name tags, weapons held in hand, gun / knife stances, attack animations, 3D weapon sounds, 5 dances and pointing.
- **Combat:** optional friendly fire with knockback, vacuums pull players and bodies.
- **Death:** while a teammate is alive you spectate them instead of ending the day; your ragdoll stays behind for everyone.
- **Menus:** multiplayer screen, lobby, pause-menu player list with kick, Tab scoreboard, all in the game's own style. Shared cursors on the end-of-day screens, with a marker on the tab each player is on.
- **Stats:** kills and deaths per player, saved with the save file.
- **Fixes:** stuck creatures behind the truck are rescued, the day-end elevators wait for everyone.

---

## Troubleshooting

- **"Mod version mismatch"**: everyone needs the same release.
- **"Couldn't reach the host"**: wrong IP or port, port not forwarded (must be UDP), or firewall.
- **"Session is full"**: the host's max players is reached.
- **After a game update** the mod may need an update too; BepInEx rebuilds its files on the first launch after an update (that launch is slow).
- Logs: `BepInEx/LogOutput.log` in the game folder.

---

## Building from source

`source/CanEmAllMP` is the main mod, `source/CanEmAllMPModels` the model add-on (it references `CanEmAllMP.dll`), `source/unity-editor` the Unity editor scripts that build the `canemall_mp` asset bundle (animator setup + bundle export).

Both mods are plain C# (`langversion 10`, `-unsafe`) compiled against the game's BepInEx interop assemblies (`BepInEx/interop/*.dll`, `BepInEx/core/*.dll`, `dotnet/*.dll` from your game folder) plus the [LiteNetLib](https://github.com/RevenantX/LiteNetLib) sources. Embedded resources:
`CanEmAllMP`: `res/cursor0-7.bin` and `res/cursorS0-7.bin` as `cursorN` / `cursorSN`; `CanEmAllMPModels`: `res/headmask.bin` as `headmask`.
`source/build.sh` is the build script used during development (reference list in a `refs.txt`).
