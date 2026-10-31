#!/bin/bash
export CS2_STARTMAP="de_dust2"

if [ -f /home/steam/cs2-dedicated/game/csgo/gameinfo.gi ]; then
    if ! grep -q "csgo/addons/metamod" /home/steam/cs2-dedicated/game/csgo/gameinfo.gi; then
        sed -i '/Game_LowViolence/a \			Game	csgo/addons/metamod' /home/steam/cs2-dedicated/game/csgo/gameinfo.gi
    fi
fi
