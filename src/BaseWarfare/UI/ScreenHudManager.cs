using System;
using System.Collections.Generic;
using BaseWarfare.Managers;
using BaseWarfare.Models;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Extensions;
using CounterStrikeSharp.API.Modules.Utils;

namespace BaseWarfare.UI;

/// <summary>
/// Renders the 2D Panorama Left-Screen Tactical HUD and Interactive Centered Base Shop Dashboard
/// using Valve's native custom_hud_layout (CCSCustomHudLayout) system:
///   - 100% 2D screen-space Panorama UI (zero 3D movement jitter, zero motion blur).
///   - Supports both real mouse-cursor clicking (Listeners.OnCustomHudClicked + SetInputCaptureEnabled)
///     and keyboard controls (R to open/close, W/S to select, E to buy).
///   - Caches all per-player dialog variables and CSS classes so network updates are sent
///     ONLY when a value actually changes.
/// </summary>
public class ScreenHudManager
{
    public const string LayoutPath = "panorama/layout/custom_game/base_warfare_hud.xml";

    private readonly MatchState _state;
    private readonly ShopManager _shop;
    private readonly EconomyAndBuffManager _economy;
    private readonly WeaponPerkManager _weaponPerks;
    private readonly JungleCampManager _jungle;

    private CCSCustomHudLayout? _hudEntity;

    // State caches to avoid redundant network messages on every tick
    private readonly Dictionary<(int Slot, string PanelId, string VarName), string> _dialogVarCache = new();
    private readonly Dictionary<(int Slot, string PanelId, string ClassName), bool> _classCache = new();
    private readonly Dictionary<int, bool> _inputCaptureCache = new();

    public ScreenHudManager(
        MatchState state,
        ShopManager shop,
        EconomyAndBuffManager economy,
        WeaponPerkManager weaponPerks,
        JungleCampManager jungle)
    {
        _state = state;
        _shop = shop;
        _economy = economy;
        _weaponPerks = weaponPerks;
        _jungle = jungle;
    }

    /// <summary>
    /// Ensures the singleton custom_hud_layout entity exists on the active map.
    /// </summary>
    public CCSCustomHudLayout? EnsureHudEntity()
    {
        if (_hudEntity is { IsValid: true })
            return _hudEntity;

        var entity = Utilities.CreateEntityByName<CCSCustomHudLayout>("custom_hud_layout");
        if (entity == null || !entity.IsValid)
            return null;

        entity.StrLayout = LayoutPath;
        entity.DispatchSpawn();

        _hudEntity = entity;
        _dialogVarCache.Clear();
        _classCache.Clear();
        _inputCaptureCache.Clear();

        return _hudEntity;
    }

    public void OnTick()
    {
        var hud = EnsureHudEntity();
        if (hud == null || !hud.IsValid)
            return;

        foreach (var player in Utilities.GetPlayers())
        {
            if (player == null || !player.IsValid || player.IsBot)
                continue;

            var pawn = player.PlayerPawn.Value;
            if (!player.PawnIsAlive || pawn == null || !pawn.IsValid)
            {
                ClosePlayerShop(player);
                continue;
            }

            HandleMenuInput(player);
            UpdatePlayerPanoramaHud(hud, player);
        }
    }

    /// <summary>
    /// Handles Panorama button clicks from the client's mouse cursor (Listeners.OnCustomHudClicked).
    /// </summary>
    public void OnCustomHudClicked(CCSPlayerController player, CCSCustomHudLayout layout, string buttonId)
    {
        if (_hudEntity is not { IsValid: true } || !layout.IsValid || layout.Handle != _hudEntity.Handle)
            return;

        if (player == null || !player.IsValid || player.IsBot)
            return;

        if (buttonId == "btn_close_shop")
        {
            ClosePlayerShop(player);
            return;
        }

        if (buttonId == "btn_grant_cash")
        {
            _shop.GrantMaxCash(player);
            return;
        }

        if (buttonId == "btn_buy_hp")
        {
            _economy.TryBuyPermanentHealth(player);
            return;
        }

        if (buttonId == "btn_buy_mag")
        {
            _weaponPerks.TryBuyExtendedMag(player);
            return;
        }

        if (buttonId == "btn_spawn_camp")
        {
            _jungle.SpawnCampInFrontOfPlayer(player);
            return;
        }

        if (buttonId.StartsWith("buy_item_", StringComparison.Ordinal) &&
            int.TryParse(buttonId.AsSpan("buy_item_".Length), out int itemId))
        {
            int index = itemId - 1;
            if (index >= 0 && index < _shop.Catalog.Count)
            {
                _state.SelectedShopIndex[player.Slot] = index;
                _shop.TryBuyItemById(player, itemId);
            }
        }
    }

    /// <summary>
    /// Handles R (open/close shop), W/S (move highlight up/down), and E (purchase item) when inside Home Base.
    /// </summary>
    private void HandleMenuInput(CCSPlayerController player)
    {
        int slot = player.Slot;
        PlayerButtons currentButtons = player.Buttons;
        _state.PreviousButtons.TryGetValue(slot, out PlayerButtons prevButtons);
        _state.PreviousButtons[slot] = currentButtons;

        bool inHomeBase = _state.IsPlayerInFriendlyBase(player);
        if (!inHomeBase)
        {
            // Automatically close shop and release mouse cursor the instant player leaves Home Base buyzone
            _state.ShopOpenPlayers.Remove(slot);
            return;
        }

        if (!_state.SelectedShopIndex.ContainsKey(slot))
            _state.SelectedShopIndex[slot] = 0;

        bool pressedReload = (currentButtons & PlayerButtons.Reload) != 0 && (prevButtons & PlayerButtons.Reload) == 0;
        if (pressedReload)
        {
            if (_state.ShopOpenPlayers.Contains(slot))
                _state.ShopOpenPlayers.Remove(slot);
            else
                _state.ShopOpenPlayers.Add(slot);
        }

        if (!_state.ShopOpenPlayers.Contains(slot))
            return;

        int catalogCount = _shop.Catalog.Count;
        bool pressedForward = (currentButtons & PlayerButtons.Forward) != 0 && (prevButtons & PlayerButtons.Forward) == 0;
        bool pressedBack    = (currentButtons & PlayerButtons.Back) != 0    && (prevButtons & PlayerButtons.Back) == 0;
        bool pressedUse     = (currentButtons & PlayerButtons.Use) != 0     && (prevButtons & PlayerButtons.Use) == 0;

        if (pressedForward)
        {
            _state.SelectedShopIndex[slot] = (_state.SelectedShopIndex[slot] - 1 + catalogCount) % catalogCount;
        }
        else if (pressedBack)
        {
            _state.SelectedShopIndex[slot] = (_state.SelectedShopIndex[slot] + 1) % catalogCount;
        }

        if (pressedUse)
        {
            int selectedIdx = _state.SelectedShopIndex[slot];
            if (selectedIdx >= 0 && selectedIdx < catalogCount)
            {
                _shop.TryBuyItem(player, _shop.Catalog[selectedIdx]);
            }
        }
    }

    private void UpdatePlayerPanoramaHud(CCSCustomHudLayout hud, CCSPlayerController player)
    {
        int slot = player.Slot;
        int cash = _economy.GetCash(player);
        bool inHomeBase = _state.IsPlayerInFriendlyBase(player);
        bool shopOpen = inHomeBase && _state.ShopOpenPlayers.Contains(slot);

        // 1. Update Left-Screen Tactical HUD variables
        SetDialogVarCached(hud, player, "HudCashLabel", "hud_cash", $"${cash:N0}");
        SetDialogVarCached(hud, player, "CtStatsLabel", "ct_stats", $"KILLS: {_state.CtKills} / {MatchState.TargetKills}   •   CAP: {_state.CtCapturePercent:0}%");
        SetDialogVarCached(hud, player, "TStatsLabel",  "t_stats",  $"KILLS: {_state.TKills} / {MatchState.TargetKills}   •   CAP: {_state.TCapturePercent:0}%");

        // Base under attack alert banner
        bool ctAlert = player.TeamNum == (byte)CsTeam.CounterTerrorist && _state.TsInCtBase > 0;
        bool tAlert  = player.TeamNum == (byte)CsTeam.Terrorist && _state.CtsInTBase > 0;
        bool showAlert = ctAlert || tAlert;

        string alertText = ctAlert
            ? $"!! WARNING: {_state.TsInCtBase} T CAPTURING YOUR BASE ({_state.TCapturePercent:0}%) !!"
            : tAlert
                ? $"!! WARNING: {_state.CtsInTBase} CT CAPTURING YOUR BASE ({_state.CtCapturePercent:0}%) !!"
                : "";

        SetHasClassCached(hud, player, "AlertBanner", "IsAlert", showAlert);
        if (showAlert)
        {
            SetDialogVarCached(hud, player, "AlertTextLabel", "alert_text", alertText);
        }

        // Home Base vs Combat status banner
        string statusText = inHomeBase
            ? (shopOpen ? "[IN HOME BASE — PRESS R OR CLICK CLOSE]" : "[IN HOME BASE — PRESS R FOR BASE SHOP]")
            : "[IN COMBAT — RETURN TO BASE FOR SHOP]";

        SetHasClassCached(hud, player, "StatusBanner", "InBase", inHomeBase);
        SetDialogVarCached(hud, player, "StatusTextLabel", "status_text", statusText);

        // 2. Update Centered Interactive Base Shop Dashboard & Mouse Input Capture
        SetHasClassCached(hud, player, "ShopOverlay", "IsOpen", shopOpen);
        SetInputCaptureCached(hud, player, shopOpen);

        if (shopOpen)
        {
            SetDialogVarCached(hud, player, "ShopCashDisplay", "shop_cash", $"BALANCE: ${cash:N0}");
            SetDialogVarCached(hud, player, "ShopSubTitle", "shop_subtitle", BuildActivePerksSummary(player));

            int selectedIdx = _state.SelectedShopIndex.TryGetValue(slot, out int idx) ? idx : 0;
            for (int i = 0; i < _shop.Catalog.Count; i++)
            {
                string btnId = $"buy_item_{i + 1}";
                SetHasClassCached(hud, player, btnId, "Selected", i == selectedIdx);
            }
        }
    }

    private string BuildActivePerksSummary(CCSPlayerController player)
    {
        int slot = player.Slot;
        var perks = new List<string>();

        int bonusHp = _economy.GetBonusHealth(player);
        if (bonusHp > 0) perks.Add($"+{bonusHp} Perm HP");

        if (_state.SpeedPlayers.Contains(slot))      perks.Add("Speed 1.35x");
        if (_state.JuggernautPlayers.Contains(slot)) perks.Add("Juggernaut 225HP");
        if (_state.VampirePlayers.Contains(slot))    perks.Add("Vampire +35HP");
        if (_state.GravityPlayers.Contains(slot))    perks.Add("Moon Gravity");

        var activeWeapon = player.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value;
        if (activeWeapon != null && activeWeapon.IsValid &&
            _weaponPerks.TryGetWeaponMod(activeWeapon.Index, out var modData) && modData != null)
        {
            perks.Add($"ExtMag {modData.BoostedMaxClip} ({modData.RemainingDeaths}/{modData.MaxDeaths}d)");
        }

        return perks.Count > 0
            ? $"Active Perks: {string.Join(" • ", perks)}"
            : "Active Perks: None (Click any Weapon or Perk below to purchase)";
    }

    private void SetDialogVarCached(
        CCSCustomHudLayout hud,
        CCSPlayerController player,
        string panelId,
        string varName,
        string value)
    {
        var key = (player.Slot, panelId, varName);
        if (_dialogVarCache.TryGetValue(key, out string? existing) && existing == value)
            return;

        hud.SetDialogVariableStringForPlayer(player, panelId, varName, value);
        _dialogVarCache[key] = value;
    }

    private void SetHasClassCached(
        CCSCustomHudLayout hud,
        CCSPlayerController player,
        string panelId,
        string className,
        bool hasClass)
    {
        var key = (player.Slot, panelId, className);
        if (_classCache.TryGetValue(key, out bool existing) && existing == hasClass)
            return;

        hud.SetHasClassForPlayer(player, panelId, className, hasClass);
        _classCache[key] = hasClass;
    }

    private void SetInputCaptureCached(
        CCSCustomHudLayout hud,
        CCSPlayerController player,
        bool enabled)
    {
        int slot = player.Slot;
        if (_inputCaptureCache.TryGetValue(slot, out bool existing) && existing == enabled)
            return;

        hud.SetInputCaptureEnabled(player, enabled);
        _inputCaptureCache[slot] = enabled;
    }

    public void ClosePlayerShop(CCSPlayerController? player)
    {
        if (player == null || !player.IsValid)
            return;

        int slot = player.Slot;
        _state.ShopOpenPlayers.Remove(slot);

        if (_hudEntity is { IsValid: true })
        {
            SetHasClassCached(_hudEntity, player, "ShopOverlay", "IsOpen", false);
            SetInputCaptureCached(_hudEntity, player, false);
        }
    }

    public void RemovePlayerHud(int slot)
    {
        _state.ShopOpenPlayers.Remove(slot);

        var player = Utilities.GetPlayerFromSlot(slot);
        if (player != null && player.IsValid && _hudEntity is { IsValid: true })
        {
            _hudEntity.SetHasClassForPlayer(player, "ShopOverlay", "IsOpen", false);
            _hudEntity.SetInputCaptureEnabled(player, false);
        }

        // Purge cached entries for this slot so reconnecting players get fresh state
        var dialogKeysToRemove = new List<(int Slot, string PanelId, string VarName)>();
        foreach (var k in _dialogVarCache.Keys)
        {
            if (k.Slot == slot)
                dialogKeysToRemove.Add(k);
        }
        foreach (var k in dialogKeysToRemove)
            _dialogVarCache.Remove(k);

        var classKeysToRemove = new List<(int Slot, string PanelId, string ClassName)>();
        foreach (var k in _classCache.Keys)
        {
            if (k.Slot == slot)
                classKeysToRemove.Add(k);
        }
        foreach (var k in classKeysToRemove)
            _classCache.Remove(k);

        _inputCaptureCache.Remove(slot);
    }

    public void CleanupAll()
    {
        if (_hudEntity is { IsValid: true })
        {
            foreach (var player in Utilities.GetPlayers())
            {
                if (player != null && player.IsValid && !player.IsBot)
                {
                    _hudEntity.SetInputCaptureEnabled(player, false);
                    _hudEntity.SetHasClassForPlayer(player, "ShopOverlay", "IsOpen", false);
                }
            }

            _hudEntity.Remove();
        }

        _hudEntity = null;
        _dialogVarCache.Clear();
        _classCache.Clear();
        _inputCaptureCache.Clear();
    }
}
