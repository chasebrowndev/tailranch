# TailRanch

Co-op for Slime Rancher 1 (v1.4.4) over [Tailscale](https://tailscale.com). One player hosts their save; friends on the same tailnet join it. No port forwarding, no public server.

The host is authoritative: it simulates the world (slimes, spawners, ranch automation, clock), and clients send their actions to it. Synced:

- player avatars with nametags, map markers per zone
- slimes, plorts, food and everything else you shoot or vac
- shared money and keys, doors, progress, mail
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

Only tailnet addresses (`100.64.0.0/10`, `fd7a:115c:a1e0::/48`) are accepted. UDP port 7777 by default.

## Config

`BepInEx/config/` → TailRanch section:

| Key | Default | |
|---|---|---|
| `Net.Port` | 7777 | UDP port the host listens on |
| `Net.PlayerName` | | Name above your avatar |
| `Net.JoinTarget` | | Last joined host |

## Build

Requires the .NET SDK and a Slime Rancher install with BepInEx.

```sh
dotnet build -p:GameDir="/path/to/Slime Rancher"
```

The build copies the DLL into the game's `BepInEx/plugins/TailRanch/`. `scripts/sr.sh` runs two local instances under Proton for testing.

## License

MIT. Bundles [LiteNetLib](https://github.com/RevenantX/LiteNetLib) (MIT).
