# ==============================================================================
# Base Warfare UI — Windows CS2 Panorama Compiler Script
# Run this script in PowerShell on your Windows PC after enabling CS2 Workshop Tools
# ==============================================================================

param(
    [string]$Cs2Root = "C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive",
    [string]$AddonName = "base_warfare_ui"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path "$Cs2Root\game\bin\win64\resourcecompiler.exe")) {
    Write-Host "[!] Could not find resourcecompiler.exe at: $Cs2Root\game\bin\win64\resourcecompiler.exe" -ForegroundColor Red
    Write-Host "    1. Make sure 'Counter-Strike 2 Workshop Tools' DLC is checked in Steam." -ForegroundColor Yellow
    Write-Host "    2. If your CS2 is installed on another drive (e.g. D:\SteamLibrary\...), pass -Cs2Root 'D:\...\Counter-Strike Global Offensive'" -ForegroundColor Yellow
    exit 1
}

$ContentDir = "$Cs2Root\content\csgo_addons\$AddonName\panorama"
$LayoutDir  = "$ContentDir\layout\custom_game"
$StylesDir  = "$ContentDir\styles\custom_game"

New-Item -ItemType Directory -Force -Path $LayoutDir | Out-Null
New-Item -ItemType Directory -Force -Path $StylesDir | Out-Null

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$SrcXml = "$ScriptDir\content\csgo_addons\base_warfare_ui\panorama\layout\custom_game\base_warfare_hud.xml"
$SrcCss = "$ScriptDir\content\csgo_addons\base_warfare_ui\panorama\styles\custom_game\base_warfare_hud.css"

Copy-Item -Path $SrcXml -Destination "$LayoutDir\base_warfare_hud.xml" -Force
Copy-Item -Path $SrcCss -Destination "$StylesDir\base_warfare_hud.css" -Force

Write-Host "[+] Copied base_warfare_hud.xml and base_warfare_hud.css to $ContentDir" -ForegroundColor Cyan

$Compiler = "$Cs2Root\game\bin\win64\resourcecompiler.exe"
Write-Host "[*] Compiling Panorama CSS & XML into .vcss_c and .vxml_c..." -ForegroundColor Cyan

& $Compiler -i "$StylesDir\base_warfare_hud.css"
& $Compiler -i "$LayoutDir\base_warfare_hud.xml"

$CompiledXml = "$Cs2Root\game\csgo_addons\$AddonName\panorama\layout\custom_game\base_warfare_hud.vxml_c"
$CompiledCss = "$Cs2Root\game\csgo_addons\$AddonName\panorama\styles\custom_game\base_warfare_hud.vcss_c"

if ((Test-Path $CompiledXml) -and (Test-Path $CompiledCss)) {
    Write-Host "[SUCCESS] Compiled successfully!" -ForegroundColor Green
    Write-Host "  -> $CompiledXml"
    Write-Host "  -> $CompiledCss"
    Write-Host ""
    Write-Host "Next Step: Open CS2 Workshop Tools -> Workshop Manager -> Publish '$AddonName' and copy the Workshop ID!" -ForegroundColor Yellow
} else {
    Write-Host "[!] Compilation finished, check output above if .vxml_c / .vcss_c were not created." -ForegroundColor Red
}

