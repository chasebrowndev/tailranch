#!/usr/bin/env bash
# Two Slime Rancher instances on one machine through Proton.
#   sr.sh a      host instance (auto-loads newest save and hosts)
#   sr.sh b      client instance (auto-joins A)
#   sr.sh both   both, B after a delay
#   sr.sh logs   tail both BepInEx logs
# Env: TARGET (join target for B, default 127.0.0.1), NOAUTO=1 to skip auto host/join, W/H window size (1280x720).
set -euo pipefail

GAME_A="${GAME_DIR:-$HOME/Downloads/slimerancher/Slime Rancher v1.4.4}"
STATE="$HOME/.local/share/tailranch"
GAME_B="$STATE/sr-b"
STEAM="$HOME/.local/share/Steam"
PROTON="$STEAM/steamapps/common/Proton - Experimental/proton"
DLL="$(dirname "$0")/../bin/Debug/net472/TailRanch.dll"

sync_b() {
	if [[ ! -d "$GAME_B" ]]; then
		echo "creating instance B at $GAME_B (hardlinked)"
		mkdir -p "$STATE"
		cp -al "$GAME_A" "$GAME_B"
		# BepInEx rewrites files in place (config, cache, log); give B real copies.
		rm -rf "$GAME_B/BepInEx"
		cp -a "$GAME_A/BepInEx" "$GAME_B/BepInEx"
		rm -rf "$GAME_B/BepInEx/config" "$GAME_B/BepInEx/cache" "$GAME_B/BepInEx/LogOutput.log"
	fi
	if [[ -f "$DLL" ]]; then
		for g in "$GAME_A" "$GAME_B"; do
			mkdir -p "$g/BepInEx/plugins/TailRanch"
			cp "$DLL" "$g/BepInEx/plugins/TailRanch/"
		done
	fi
}

launch() { # name dir env...
	local name=$1 dir=$2; shift 2
	mkdir -p "$STATE/pfx-$name"
	(
		cd "$dir"
		env STEAM_COMPAT_CLIENT_INSTALL_PATH="$STEAM" \
			STEAM_COMPAT_DATA_PATH="$STATE/pfx-$name" \
			WINEDLLOVERRIDES="winhttp=n,b" \
			TAILRANCH_LOOPBACK=1 TAILRANCH_STATS=1 "$@" \
			"$PROTON" run "$dir/SlimeRancher.exe" -screen-fullscreen 0 -screen-width "${W:-1280}" -screen-height "${H:-720}" \
			>"$STATE/proton-$name.log" 2>&1 &
	)
	echo "launched $name (proton log: $STATE/proton-$name.log, bepinex log: $dir/BepInEx/LogOutput.log)"
}

auto_a=(TAILRANCH_NAME=host)
auto_b=(TAILRANCH_NAME=client)
if [[ "${NOAUTO:-}" != 1 ]]; then
	auto_a+=(TAILRANCH_AUTOHOST=1)
	auto_b+=(TAILRANCH_AUTOJOIN="${TARGET:-127.0.0.1}")
fi

case "${1:-}" in
	a) sync_b; launch a "$GAME_A" "${auto_a[@]}" ;;
	b) sync_b; launch b "$GAME_B" "${auto_b[@]}" ;;
	both)
		sync_b
		launch a "$GAME_A" "${auto_a[@]}"
		sleep "${DELAY:-25}"
		launch b "$GAME_B" "${auto_b[@]}"
		;;
	logs) tail -F "$GAME_A/BepInEx/LogOutput.log" "$GAME_B/BepInEx/LogOutput.log" ;;
	*) sed -n 2,8p "$0"; exit 1 ;;
esac
