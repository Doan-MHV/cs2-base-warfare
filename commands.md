# CS2 Playground — Server & Modding Cheat Sheet

## 1. Server Control Commands (Run in Server Terminal)

| Action | Command |
| :--- | :--- |
| **Start server & watch live console** | `docker start -a cs2-dedicated` |
| **Start server in background (24/7)** | `docker start cs2-dedicated` |
| **Stop the CS2 server** | `docker stop cs2-dedicated` |
| **Restart server (also auto-updates CS2)** | `docker restart cs2-dedicated` |
| **Watch live logs (`Ctrl+C` to exit view)** | `docker logs -f --tail 100 cs2-dedicated` |
| **Check running containers** | `docker ps` |
| **Fix file permissions if IDE cannot save** | `sudo chown -R $USER:$USER /srv/cs2-data && sudo chmod -R 777 /srv/cs2-data` |
| **Shut down the GCP VM (stops hourly billing)** | `sudo poweroff` |

---

## 2. Build & Hot-Reload Your C# Plugin

CounterStrikeSharp automatically watches the `plugins/BaseWarfare` folder. Whenever you save changes to any `.cs` file in `/srv/cs2-data/src/BaseWarfare`, run this command to compile and **instantly hot-reload** your plugin in-game without restarting the server:

```bash
~/.dotnet/dotnet build /srv/cs2-data/src/BaseWarfare/BaseWarfare.csproj -c Release -o /srv/cs2-data/game/csgo/addons/counterstrikesharp/plugins/BaseWarfare
```

---

## 3. In-Game CS2 Console Commands (`~`)

| Action | Console Command |
| :--- | :--- |
| **Connect to server** | `connect <YOUR_SERVER_IP>:27015` |
| **Log in to RCON (Admin)** | `rcon_password <YOUR_RCON_PASSWORD>` |
| **List loaded C# plugins** | `rcon css_plugins list` |
| **Reload BaseWarfare plugin manually** | `rcon css_plugins reload BaseWarfare` |
| **List Metamod plugins** | `rcon meta list` |
| **Change map** | `rcon changelevel de_mirage` (`de_dust2`, `de_inferno`, `de_nuke`, `de_anubis`) |
| **Restart match** | `rcon mp_restartgame 1` |
| **Add / Kick bots** | `rcon bot_add_ct` / `rcon bot_add_t` / `rcon bot_kick` |
| **Check server CPU & tickrate** | `rcon stats` |
| **Show Ping, Packet Loss & Net Jitter HUD** | `cl_hud_telemetry_frametime_show 2; cl_hud_telemetry_ping_show 2; cl_hud_telemetry_net_misdelivery_show 2; cl_hud_telemetry_serverrecvmargin_graph_show 2` |

---

## 4. In-Game Controls & Chat Commands (Base Warfare v2.3.0 — 2D Panorama UI)

### 🖥️ Interactive 2D Panorama Base Shop Controls (While Inside Home Base Buyzone)
* **`R` (Reload key) or `!shop`** — Open / Close the 2D Centered Base Shop Dashboard (enables mouse cursor)
* **Mouse Left-Click** — Click any Weapon (`AK-47`, `M4A1-S`, `AWP`, `Deagle`, `MP9`), Perk (`Speed`, `Juggernaut`, `Vampire`, `Moon Gravity`), `+ GRANT $30,000 TEST CASH`, or `CLOSE [R]` button directly
* **`W` / `S` (Forward / Back) + `E` (Use key)** — Keyboard navigation & purchase also supported

### 💬 Chat Commands (`Y`) & Console Equivalents (`css_<command>`)
* **`!cash`** — Instantly set your balance to **`$30,000`** for testing
* **`!shop`** — Open / Close the 2D Base Shop Dashboard
* **`!buyhp`** — Buy **`+25 Permanent HP`** (`$1,500`, persists across deaths on `SteamID`)
* **`!buymag`** — Upgrade active weapon with **`+30% Extended Mag`** (`$1,200`, `2-Death Durability`)
* **`!spawncamp`** — Spawn a **Zero-Spike Neutral Jungle Creep Camp** (`300 HP`, `+$250 Bounty`, `30s Respawn`)
* **`!bwreset`** — Reset Kill/Capture scores, RPG buffs, Jungle creeps, `custom_hud_layout`, and CVars

#### 🔫 Weapons (Must be inside Home Base Buyzone)
* **`!buy1`** / **`!g1`** / **`!ak`** — Buy **AK-47** (`$2700`)
* **`!buy2`** / **`!g2`** / **`!m4`** — Buy **M4A1-S** (`$2900`)
* **`!buy3`** / **`!g3`** / **`!awp`** — Buy **AWP** (`$4750`)
* **`!buy4`** / **`!g4`** / **`!deagle`** — Buy **Desert Eagle** (`$700`)
* **`!buy5`** / **`!g5`** / **`!mp9`** — Buy **MP9 + Kevlar** (`$1250`)

#### ⚡ Perks / Power-Ups (Must be inside Home Base Buyzone)
* **`!buy6`** / **`!speed`** — Buy **Speed Boost 1.35x** (`$2000`)
* **`!buy7`** / **`!jugg`** — Buy **Juggernaut 225 HP + Helmet & Kevlar** (`$3000`)
* **`!buy8`** / **`!vamp`** — Buy **Vampire Lifesteal +35 HP/kill** (`$2500`)
* **`!buy9`** / **`!grav`** — Buy **Moon Gravity 0.55x** (`$1500`)

---

## 5. Project Architecture & Key Paths (`/srv/cs2-data`)

* `/srv/cs2-data/src/BaseWarfare/` — **BaseWarfare v2.4.0 C# Source Directory**
  * `BaseWarfare.csproj` — `.NET 10.0` project file referencing `CounterStrikeSharp.API.dll`
  * `BaseWarfarePlugin.cs` — Main plugin entry point, listeners (`OnCustomHudClicked`, `OnServerPrecacheResources`, `OnEntityDeleted`), event hooks, and command registration
  * `Models/MatchState.cs` — Match scores, capture state, active perks, and `IsPlayerInFriendlyBase` (`pawn.InBuyZone`)
  * `Models/RpgModels.cs` — `PlayerRpgState`, `WeaponModData` (`2-Death Durability`), and `OptimizedCreepData`
  * `Managers/EconomyAndBuffManager.cs` — `SteamID`-keyed cash & permanent `BonusHealth` manager (`!buyhp`)
  * `Managers/WeaponPerkManager.cs` — Entity `Index`-keyed `+30% Extended Mag` & `2-Death Durability` manager (`!buymag`)
  * `Managers/JungleCampManager.cs` — Zero-Spike `prop_dynamic_override` neutral jungle creep manager (`!spawncamp`)
  * `Managers/GameModeManager.cs` — Game mode CVars, 5v5 spawn cluster detection (`LocateTeamBases`), kill rewards, Stim Pack distribution, and capture loop
  * `Managers/ShopManager.cs` — Weapon & Perk catalog, clean weapon slot replacement, and `!cash` logic
  * `UI/ScreenHudManager.cs` — 2D Panorama `CCSCustomHudLayout` (`panorama/layout/custom_game/base_warfare_hud.xml`) + mouse click & `R`/`W`/`S`/`E` shop handler
* `/srv/cs2-data/workshop_ui/` — **2D Panorama Workshop Addon Source & Guide**
  * `content/csgo_addons/base_warfare_ui/panorama/layout/custom_game/base_warfare_hud.xml` — Panorama XML layout
  * `content/csgo_addons/base_warfare_ui/panorama/styles/custom_game/base_warfare_hud.css` — Panorama CSS stylesheet
  * `install_and_compile_windows.ps1` — Windows PowerShell script to copy & compile `.xml`/`.css` -> `.vxml_c`/`.vcss_c`
  * `WORKSHOP_GUIDE.md` — Step-by-step instructions to publish `base_warfare_ui` to Steam Workshop
* `/srv/cs2-data/game/csgo/cfg/multiaddonmanager/multiaddonmanager.cfg` — **MultiAddonManager Config** (`mm_extra_addons` & `mm_client_extra_addons`)
* `/srv/cs2-data/game/csgo/addons/counterstrikesharp/plugins/BaseWarfare/` — Compiled plugin output folder
* `/srv/cs2-data/game/csgo/addons/counterstrikesharp/configs/admins.json` — Admin permissions config (`<YOUR_STEAM_ID>`)
* `/srv/cs2-data/pre.sh` — Pre-start hook that ensures `Game csgo/addons/metamod` stays in `gameinfo.gi`

---

## 6. SSH Config (`~/.ssh/config` on Windows & MacBook)

Whenever you stop and start the GCP VM, update `HostName` in `~/.ssh/config` to the new External IP:

```ssh-config
Host cs2-playground
    HostName <YOUR_SERVER_IP>
    User <YOUR_SSH_USER>
    IdentityFile ~/.ssh/cs2_gcp
    StrictHostKeyChecking no
```

---

## 7. Recreate Docker Container (Change Server Name / Password / Map)

```bash
docker rm -f cs2-dedicated && \
docker run -d \
  --name cs2-dedicated \
  --restart unless-stopped \
  -e SRCDS_TOKEN="<YOUR_STEAM_GSLT_TOKEN>" \
  -e CS2_SERVERNAME="CS2 playground" \
  -e CS2_RCONPW="<YOUR_RCON_PASSWORD>" \
  -e CS2_PW="" \
  -e CS2_GAMETYPE=0 \
  -e CS2_GAMEMODE=1 \
  -e CS2_MAPGROUP="mg_active" \
  -e CS2_STARTMAP="de_dust2" \
  -e CS2_MAXPLAYERS=16 \
  -p 27015:27015/tcp \
  -p 27015:27015/udp \
  -p 27020:27020/udp \
  -v /srv/cs2-data:/home/steam/cs2-dedicated/ \
  joedwards32/cs2
```
