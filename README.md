# TailRanch

Co-op for Slime Rancher 1 (v1.4.4) over [Tailscale](https://tailscale.com). One player hosts their save; friends on the same tailnet join it. No port forwarding, no public server.

The host is authoritative: it simulates the world (slimes, spawners, ranch automation, clock), and clients send their actions to it. Synced:

- player avatars with nametags, map markers per zone
- slimes, plorts, food and everything else you shoot or vac
- money and keys, doors, progress, mail (shared by default, or per player; see Hosting)
- plots and upgrades, silos, feeders, gadgets, refinery
- in-game chat

## Install

1. Install [BepInEx 5](https://github.com/BepInEx/BepInEx/releases) (x64) into the Slime Rancher folder and launch the game once.
2. Download `TailRanch.dll` from [Releases](https://github.com/chasebrowndev/tailranch/releases) into `BepInEx/plugins/TailRanch/`.
3. Every player needs Tailscale running and to be on the same tailnet, all on the same TailRanch version and Slime Rancher 1.4.4 (the final SR1 release). Other game versions are refused at join, and the main menu warns if your build is unsupported.

## Play

- **Host:** load your save, press Esc, and choose **Host this world** in the co-op panel. Share the address it shows (click to copy).
- **Join:** on the main menu, enter the host's MagicDNS name (e.g. `framework`) or `100.x` IP and press **Join**. Recent hosts are remembered.
- **Chat:** Enter to open, Enter to send, Esc to close.

## Hosting

The host's co-op panel (Esc) lists everyone connected. On each row, **×** kicks a player and **ban** bans them; the player sees the reason. A ban lasts until you stop hosting, and the **Manage** view lists bans so you can lift one.

**Manage** also has:

- **Shared:** choose what players share. Everything is on by default. Turn off Money & keys, Upgrades, Blueprints & gadgets, or Pedia, map & mail to give each player their own copy. On first join that copy starts from the host's. After that the host keeps it for each save and player (it keeps following the shared values while the category is shared), under `BepInEx/config/TailRanch/profiles/<save>/<player id>.dat`. The player id is the auto-generated `Net.PlayerId`, so renaming yourself doesn't lose your progress. You can only change these while nobody is connected.
- **Access:** a password joiners must enter (the **Pass** field on their main menu). Leave it empty to let anyone on your tailnet join.

Only tailnet addresses (`100.64.0.0/10`, `fd7a:115c:a1e0::/48`) are accepted. UDP port 7777 by default.

## Config

`BepInEx/config/` → TailRanch section:

| Key | Default | |
|---|---|---|
| `Net.Port` | 7777 | UDP port the host listens on |
| `Net.PlayerName` | | Name above your avatar (also editable in the co-op panel, live mid-session) |
| `Net.JoinTarget` | | Last joined host |
| `Net.JoinPassword` | | Password sent when joining |
| `Net.Password` | | Host: password joiners must give (empty = open) |
| `Net.PlayerId` | generated | Your stable id; the host keys your unshared progress by it |
| `Share.Money` / `Upgrades` / `Blueprints` / `Progress` | true | Host: what players share (also in Manage) |

## Build

Requires the .NET SDK and a Slime Rancher install with BepInEx.

```sh
dotnet build -p:GameDir="/path/to/Slime Rancher"
```

The build copies the DLL into the game's `BepInEx/plugins/TailRanch/`. `scripts/sr.sh` runs two local instances under Proton for testing.

## License

MIT. Bundles [LiteNetLib](https://github.com/RevenantX/LiteNetLib) (MIT).
