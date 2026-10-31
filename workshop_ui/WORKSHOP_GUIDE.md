# Base Warfare 2D Panorama UI — Workshop Addon Guide

This folder (`/srv/cs2-data/workshop_ui`) contains everything needed to compile and publish your own **2D Panorama Custom HUD & Interactive Base Shop** (`base_warfare_ui`) to the Steam Workshop.

---

## Step 1: Enable CS2 Workshop Tools on Windows
1. Open **Steam** on your Windows PC -> right-click **Counter-Strike 2** -> **Properties** -> **DLC**.
2. Check **Counter-Strike 2 Workshop Tools** and let Steam finish downloading.
3. Launch **Counter-Strike 2 Workshop Tools** once and click **Create New Addon** -> name it:
   ```text
   base_warfare_ui
   ```

---

## Step 2: Copy & Compile `base_warfare_hud.xml` and `base_warfare_hud.css`
1. Copy the two files from this server to your Windows PC:
   * `/srv/cs2-data/workshop_ui/content/csgo_addons/base_warfare_ui/panorama/layout/custom_game/base_warfare_hud.xml`
     -> `C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive\content\csgo_addons\base_warfare_ui\panorama\layout\custom_game\base_warfare_hud.xml`
   * `/srv/cs2-data/workshop_ui/content/csgo_addons/base_warfare_ui/panorama/styles/custom_game/base_warfare_hud.css`
     -> `C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive\content\csgo_addons\base_warfare_ui\panorama\styles\custom_game\base_warfare_hud.css`
2. Open **PowerShell** on your Windows PC and run `resourcecompiler.exe` to generate `.vcss_c` and `.vxml_c`:
   ```powershell
   $Cs2 = "C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive"
   & "$Cs2\game\bin\win64\resourcecompiler.exe" -i "$Cs2\content\csgo_addons\base_warfare_ui\panorama\styles\custom_game\base_warfare_hud.css"
   & "$Cs2\game\bin\win64\resourcecompiler.exe" -i "$Cs2\content\csgo_addons\base_warfare_ui\panorama\layout\custom_game\base_warfare_hud.xml"
   ```
   Verify that these two compiled files now exist on your Windows PC:
   * `...\game\csgo_addons\base_warfare_ui\panorama\layout\custom_game\base_warfare_hud.vxml_c`
   * `...\game\csgo_addons\base_warfare_ui\panorama\styles\custom_game\base_warfare_hud.vcss_c`

---

## Step 3: Publish to Steam Workshop & Get Your Workshop ID
1. In **CS2 Workshop Tools** (`base_warfare_ui`), open **Hammer** (Map Editor), create a simple empty box room (`File -> New`, add a block + `info_player_start`, save as `content\csgo_addons\base_warfare_ui\maps\base_warfare_ui.vmap`, and build/compile the map with `F9`).
   *(Note: Valve's Workshop uploader requires a compiled `.vpk` map inside `game\csgo_addons\base_warfare_ui\maps\` when publishing an addon; the compiler automatically packs your compiled `panorama\...\base_warfare_hud.vxml_c` and `.vcss_c` inside that addon VPK!)*
2. In Hammer, go to **File -> Upload to Workshop** (or open the **Workshop Manager**):
   * **Title:** `Base Warfare UI`
   * **Visibility:** `Unlisted` or `Public` *(do not use Private, or the dedicated server cannot download it)*
3. Click **Upload**. Once published, copy the numeric **Workshop ID** from the Steam URL:
   `https://steamcommunity.com/sharedfiles/filedetails/?id=XXXXXXXXXX`

---

## Step 4: Activate the Workshop ID on Your Server
Edit `/srv/cs2-data/game/csgo/cfg/multiaddonmanager/multiaddonmanager.cfg` and put your Workshop ID in both `mm_extra_addons` and `mm_client_extra_addons`:

```cfg
mm_extra_addons        "XXXXXXXXXX"
mm_client_extra_addons "XXXXXXXXXX"
```

Then restart the container (or run `rcon mm_add_addon XXXXXXXXXX` + `rcon changelevel de_mirage`), and every player who connects will automatically download and see the 2D Panorama HUD & Clickable Shop!

