using System;
using System.Collections.Generic;
using System.Linq;
using BaseWarfare.Models;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace BaseWarfare.Managers;

/// <summary>
/// Manages the Base Warfare Weapon & Perk Shop, weapon slot replacement,
/// Home Base validation, shop toggle, and the !cash testing command.
/// Unified with EconomyAndBuffManager for player cash tracking.
/// </summary>
public class ShopManager
{
    private readonly MatchState _state;
    private readonly EconomyAndBuffManager _economy;

    public IReadOnlyList<ShopItem> Catalog { get; } = new List<ShopItem>
    {
        // Weapons (1 - 5)
        new(1, "AK-47",            "ak",     2700, true,  "weapon_ak47",          "One-tap headshot rifle"),
        new(2, "M4A1-S",           "m4",     2900, true,  "weapon_m4a1_silencer", "Silenced rifle"),
        new(3, "AWP",              "awp",    4750, true,  "weapon_awp",           "One-shot sniper rifle"),
        new(4, "Deagle",           "deagle", 700,  true,  "weapon_deagle",        "Heavy pistol"),
        new(5, "MP9 + Kevlar",     "mp9",    1250, true,  "weapon_mp9",           "High-mobility SMG + Kevlar"),

        // Perks (6 - 9)
        new(6, "Speed 1.35x",      "speed",  2000, false, null,                   "Move 35% faster all life"),
        new(7, "Juggernaut 225HP", "jugg",   3000, false, null,                   "225 HP + Helmet & Kevlar"),
        new(8, "Vampire +35HP",    "vamp",   2500, false, null,                   "Heal +35 HP on every kill"),
        new(9, "Moon Gravity",     "grav",   1500, false, null,                   "Low-gravity 0.55x jump")
    };

    public ShopManager(MatchState state, EconomyAndBuffManager economy)
    {
        _state = state;
        _economy = economy;
    }

    /// <summary>
    /// Grants $30,000 to the player for testing weapons, RPG upgrades, and perks.
    /// </summary>
    public void GrantMaxCash(CCSPlayerController? player)
    {
        if (player == null || !player.IsValid) return;

        _economy.SetCash(player, MatchState.MaxMoney);

        player.PrintToChat($" {ChatColors.Green}[Base Shop]{ChatColors.Default} Cash set to {ChatColors.Gold}${MatchState.MaxMoney:N0}{ChatColors.Default}! Press {ChatColors.LightBlue}R{ChatColors.Default} in Home Base to open the Screen Shop.");
    }

    /// <summary>
    /// Toggles the on-screen shop menu when inside Home Base (triggered by !shop or R key).
    /// </summary>
    public void ToggleShop(CCSPlayerController? player)
    {
        if (player == null || !player.IsValid || !player.PawnIsAlive) return;

        if (!_state.IsPlayerInFriendlyBase(player))
        {
            _state.ShopOpenPlayers.Remove(player.Slot);
            player.PrintToChat($" {ChatColors.Red}[Base Shop]{ChatColors.Default} You must return to your {ChatColors.Yellow}Home Base Buyzone{ChatColors.Default} to open the Shop!");
            return;
        }

        if (_state.ShopOpenPlayers.Contains(player.Slot))
        {
            _state.ShopOpenPlayers.Remove(player.Slot);
        }
        else
        {
            _state.ShopOpenPlayers.Add(player.Slot);
            if (!_state.SelectedShopIndex.ContainsKey(player.Slot))
                _state.SelectedShopIndex[player.Slot] = 0;
        }
    }

    /// <summary>
    /// Attempts to purchase a shop item by 1-based catalog ID (1-9).
    /// </summary>
    public bool TryBuyItemById(CCSPlayerController? player, int itemId)
    {
        var item = Catalog.FirstOrDefault(i => i.Id == itemId);
        if (item == null) return false;
        return TryBuyItem(player, item);
    }

    /// <summary>
    /// Attempts to purchase a shop item by short code (e.g. "ak", "m4", "speed", "jugg").
    /// </summary>
    public bool TryBuyItemByCode(CCSPlayerController? player, string shortCode)
    {
        var item = Catalog.FirstOrDefault(i => i.ShortCode.Equals(shortCode, StringComparison.OrdinalIgnoreCase));
        if (item == null) return false;
        return TryBuyItem(player, item);
    }

    public bool TryBuyItem(CCSPlayerController? player, ShopItem item)
    {
        if (player == null || !player.IsValid || !player.PawnIsAlive || player.PlayerPawn.Value == null)
            return false;

        if (!_state.IsPlayerInFriendlyBase(player))
        {
            player.PrintToChat($" {ChatColors.Red}[Base Shop]{ChatColors.Default} You must stand inside your {ChatColors.Yellow}Home Base Buyzone{ChatColors.Default} to buy {item.Name}!");
            return false;
        }

        // Prevent accidental double-purchase on single E press
        double now = Server.CurrentTime;
        if (_state.LastPurchaseTime.TryGetValue(player.Slot, out double lastTime) && (now - lastTime) < 0.35)
            return false;

        int money = _economy.GetCash(player);
        if (money < item.Cost)
        {
            player.PrintToChat($" {ChatColors.Red}[Base Shop]{ChatColors.Default} Need {ChatColors.Gold}${item.Cost}{ChatColors.Default} for {item.Name} (You have ${money}). Type {ChatColors.LightBlue}!cash{ChatColors.Default} for $30,000!");
            return false;
        }

        var pawn = player.PlayerPawn.Value;

        // Check if player already has this perk active
        if (!item.IsWeapon)
        {
            if ((item.ShortCode == "speed" && _state.SpeedPlayers.Contains(player.Slot)) ||
                (item.ShortCode == "vamp" && _state.VampirePlayers.Contains(player.Slot)) ||
                (item.ShortCode == "grav" && _state.GravityPlayers.Contains(player.Slot)))
            {
                player.PrintToChat($" {ChatColors.Red}[Base Shop]{ChatColors.Default} You already have {ChatColors.LightBlue}{item.Name}{ChatColors.Default} active!");
                return false;
            }
        }

        // Deduct cash via EconomyAndBuffManager
        if (!_economy.TryDeductCash(player, item.Cost))
            return false;

        _state.LastPurchaseTime[player.Slot] = now;

        if (item.IsWeapon && item.EntityName != null)
        {
            GiveWeaponCleanly(player, pawn, item.EntityName);
            if (item.ShortCode == "mp9")
                player.GiveNamedItem("item_kevlar");
            player.PrintToChat($" {ChatColors.Green}[Base Shop]{ChatColors.Default} Purchased weapon {ChatColors.Yellow}{item.Name}{ChatColors.Default} (-${item.Cost})!");
        }
        else
        {
            ApplyPowerUp(player, pawn, item);
        }

        return true;
    }

    private void GiveWeaponCleanly(CCSPlayerController player, CCSPlayerPawn pawn, string weaponEntityName)
    {
        bool isSecondary = weaponEntityName == "weapon_deagle";
        RemoveExistingWeaponInSlot(pawn, isSecondary);
        player.GiveNamedItem(weaponEntityName);
    }

    /// <summary>
    /// Removes the player's current primary or secondary weapon before granting the newly purchased one
    /// so weapons don't pile up on the spawn floor or fail to equip.
    /// </summary>
    private static void RemoveExistingWeaponInSlot(CCSPlayerPawn pawn, bool isSecondary)
    {
        var weaponServices = pawn.WeaponServices;
        if (weaponServices?.MyWeapons == null) return;

        foreach (var handle in weaponServices.MyWeapons)
        {
            var weapon = handle.Value;
            if (weapon == null || !weapon.IsValid || string.IsNullOrEmpty(weapon.DesignerName))
                continue;

            string name = weapon.DesignerName;
            bool weaponIsPistol = name is "weapon_glock" or "weapon_hkp2000" or "weapon_usp_silencer"
                or "weapon_p250" or "weapon_fiveseven" or "weapon_tec9" or "weapon_cz75a"
                or "weapon_deagle" or "weapon_revolver" or "weapon_elite";

            bool weaponIsPrimary = !weaponIsPistol
                && !name.Contains("knife")
                && !name.Contains("bayonet")
                && !name.Contains("c4")
                && !name.Contains("grenade")
                && !name.Contains("flashbang")
                && !name.Contains("smokegrenade")
                && !name.Contains("molotov")
                && !name.Contains("incgrenade")
                && !name.Contains("decoy")
                && !name.Contains("taser")
                && !name.Contains("healthshot");

            if ((isSecondary && weaponIsPistol) || (!isSecondary && weaponIsPrimary))
            {
                weapon.Remove();
            }
        }
    }

    private void ApplyPowerUp(CCSPlayerController player, CCSPlayerPawn pawn, ShopItem item)
    {
        switch (item.ShortCode)
        {
            case "speed":
                _state.SpeedPlayers.Add(player.Slot);
                pawn.VelocityModifier = 1.35f;
                Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_flVelocityModifier");
                player.PrintToChat($" {ChatColors.Green}[Base Shop]{ChatColors.Default} Purchased {ChatColors.LightBlue}Speed 1.35x{ChatColors.Default}!");
                break;

            case "jugg":
                _state.JuggernautPlayers.Add(player.Slot);
                int bonusHp = _economy.GetBonusHealth(player);
                int targetJuggHp = EconomyAndBuffManager.DefaultBaseHealth + 225 + bonusHp;
                pawn.Health = targetJuggHp;
                pawn.MaxHealth = targetJuggHp;
                Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");
                Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iMaxHealth");
                player.GiveNamedItem("item_assaultsuit");
                player.PrintToChat($" {ChatColors.Green}[Base Shop]{ChatColors.Default} Purchased {ChatColors.LightBlue}Juggernaut ({targetJuggHp} HP){ChatColors.Default}!");
                break;

            case "vamp":
                _state.VampirePlayers.Add(player.Slot);
                player.PrintToChat($" {ChatColors.Green}[Base Shop]{ChatColors.Default} Purchased {ChatColors.Red}Vampire +35HP{ChatColors.Default}!");
                break;

            case "grav":
                _state.GravityPlayers.Add(player.Slot);
                pawn.GravityScale = 0.55f;
                player.PrintToChat($" {ChatColors.Green}[Base Shop]{ChatColors.Default} Purchased {ChatColors.Purple}Moon Gravity{ChatColors.Default}!");
                break;
        }
    }
}
