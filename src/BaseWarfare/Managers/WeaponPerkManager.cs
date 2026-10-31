using System;
using System.Collections.Generic;
using BaseWarfare.Models;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace BaseWarfare.Managers;

/// <summary>
/// Manages weapon-bound RPG upgrades (+30% Extended Mag capacity) and 2-Death Durability
/// keyed by uint weapon entity Index.
/// </summary>
public class WeaponPerkManager
{
    public const int DefaultMagUpgradeCost = 1200;
    public const float DefaultMagMultiplier = 1.30f;
    public const int DefaultMaxDeaths = 2;

    private readonly BasePlugin _plugin;
    private readonly EconomyAndBuffManager _economy;

    private readonly Dictionary<uint, WeaponModData> _moddedWeapons = new();

    public WeaponPerkManager(BasePlugin plugin, EconomyAndBuffManager economy)
    {
        _plugin = plugin;
        _economy = economy;
    }

    public bool TryGetWeaponMod(uint weaponIndex, out WeaponModData? modData)
    {
        return _moddedWeapons.TryGetValue(weaponIndex, out modData);
    }

    /// <summary>
    /// Decoupled purchase method for +30% Extended Mag on the player's currently active weapon
    /// (callable from css_buymag and CCSCustomHudLayout button click callbacks).
    /// </summary>
    public bool TryBuyExtendedMag(CCSPlayerController? player, int cost = DefaultMagUpgradeCost)
    {
        if (player == null || !player.IsValid || !player.PawnIsAlive)
            return false;

        var pawn = player.PlayerPawn.Value;
        var activeWeapon = pawn?.WeaponServices?.ActiveWeapon.Value;
        if (activeWeapon == null || !activeWeapon.IsValid)
        {
            player.PrintToChat($" {ChatColors.Red}[RPG Shop]{ChatColors.Default} Hold a valid firearm to upgrade its magazine!");
            return false;
        }

        if (_moddedWeapons.ContainsKey(activeWeapon.Index))
        {
            player.PrintToChat($" {ChatColors.Red}[RPG Shop]{ChatColors.Default} This weapon already has an {ChatColors.LightBlue}Extended Mag (+30%){ChatColors.Default} installed!");
            return false;
        }

        var weaponBase = activeWeapon.As<CCSWeaponBase>();
        int baseMaxClip = weaponBase?.VData?.MaxClip1 ?? 0;
        if (baseMaxClip <= 0)
        {
            player.PrintToChat($" {ChatColors.Red}[RPG Shop]{ChatColors.Default} Cannot install an Extended Mag on this item!");
            return false;
        }

        int currentCash = _economy.GetCash(player);
        if (currentCash < cost)
        {
            player.PrintToChat(
                $" {ChatColors.Red}[RPG Shop]{ChatColors.Default} Need {ChatColors.Gold}${cost}{ChatColors.Default} " +
                $"for Extended Mag +30% (You have ${currentCash})."
            );
            return false;
        }

        if (!_economy.TryDeductCash(player, cost))
            return false;

        var modData = new WeaponModData
        {
            MagMultiplier = DefaultMagMultiplier,
            BaseMaxClip = baseMaxClip,
            DeathCount = 0,
            MaxDeaths = DefaultMaxDeaths
        };

        _moddedWeapons[activeWeapon.Index] = modData;

        activeWeapon.Clip1 = modData.BoostedMaxClip;
        Utilities.SetStateChanged(activeWeapon, "CBasePlayerWeapon", "m_iClip1");

        player.PrintToChat(
            $" {ChatColors.Green}[RPG Shop]{ChatColors.Default} Upgraded {ChatColors.Yellow}{activeWeapon.DesignerName}{ChatColors.Default} " +
            $"with {ChatColors.LightBlue}Extended Mag (+30%){ChatColors.Default}: {ChatColors.Gold}{modData.BaseMaxClip} -> {modData.BoostedMaxClip}{ChatColors.Default} rounds " +
            $"({ChatColors.LightRed}{modData.RemainingDeaths}/{modData.MaxDeaths} deaths durability{ChatColors.Default})!"
        );
        return true;
    }

    /// <summary>
    /// On EventWeaponReload, waits for reload completion and pulls the extra 30% bullets
    /// from ReserveAmmo[0] into Clip1, then networks both m_iClip1 and m_pReserveAmmo.
    /// </summary>
    public HookResult HandleWeaponReload(EventWeaponReload @event)
    {
        var player = @event.Userid;
        if (player == null || !player.IsValid || !player.PawnIsAlive)
            return HookResult.Continue;

        var activeWeapon = player.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value;
        if (activeWeapon == null || !activeWeapon.IsValid)
            return HookResult.Continue;

        uint weaponIndex = activeWeapon.Index;
        if (!_moddedWeapons.TryGetValue(weaponIndex, out var modData))
            return HookResult.Continue;

        _plugin.AddTimer(2.35f, () =>
        {
            if (player == null || !player.IsValid || !player.PawnIsAlive)
                return;

            var currentWeapon = player.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value;
            if (currentWeapon == null || !currentWeapon.IsValid || currentWeapon.Index != weaponIndex)
                return;

            if (!_moddedWeapons.TryGetValue(weaponIndex, out var currentMod))
                return;

            int targetClip = currentMod.BoostedMaxClip;
            if (currentWeapon.Clip1 >= targetClip || currentWeapon.Clip1 < currentMod.BaseMaxClip)
                return;

            int extraNeeded = targetClip - currentWeapon.Clip1;
            int availableReserve = currentWeapon.ReserveAmmo[0];
            if (availableReserve <= 0 || extraNeeded <= 0)
                return;

            int bulletsToTransfer = Math.Min(extraNeeded, availableReserve);
            currentWeapon.Clip1 += bulletsToTransfer;
            currentWeapon.ReserveAmmo[0] -= bulletsToTransfer;

            Utilities.SetStateChanged(currentWeapon, "CBasePlayerWeapon", "m_iClip1");
            Utilities.SetStateChanged(currentWeapon, "CBasePlayerWeapon", "m_pReserveAmmo");
        });

        return HookResult.Continue;
    }

    /// <summary>
    /// On EventItemPickup, notifies the player in chat if they picked up a modded weapon,
    /// showing its boosted capacity and remaining death durability.
    /// </summary>
    public HookResult HandleItemPickup(EventItemPickup @event)
    {
        var player = @event.Userid;
        if (player == null || !player.IsValid || player.IsBot || !player.PawnIsAlive)
            return HookResult.Continue;

        string pickedItem = @event.Item ?? string.Empty;

        Server.NextFrame(() =>
        {
            if (player == null || !player.IsValid || !player.PawnIsAlive)
                return;

            var weapons = player.PlayerPawn.Value?.WeaponServices?.MyWeapons;
            if (weapons == null)
                return;

            foreach (var handle in weapons)
            {
                var weapon = handle.Value;
                if (weapon == null || !weapon.IsValid)
                    continue;

                if (!_moddedWeapons.TryGetValue(weapon.Index, out var modData))
                    continue;

                if (!string.IsNullOrEmpty(pickedItem) &&
                    !weapon.DesignerName.EndsWith(pickedItem, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                player.PrintToChat(
                    $" {ChatColors.Gold}[Modded Weapon]{ChatColors.Default} Picked up upgraded {ChatColors.Yellow}{weapon.DesignerName}{ChatColors.Default} " +
                    $"({ChatColors.LightBlue}{modData.BoostedMaxClip} Max Clip{ChatColors.Default} | " +
                    $"Durability: {ChatColors.LightRed}{modData.RemainingDeaths}/{modData.MaxDeaths} deaths left{ChatColors.Default})."
                );
                break;
            }
        });

        return HookResult.Continue;
    }

    /// <summary>
    /// Must run in HookMode.Pre on EventPlayerDeath so victim.PlayerPawn.Value.WeaponServices.MyWeapons
    /// is inspected BEFORE CS2 drops weapons on death. Increments DeathCount and destroys the weapon
    /// on Server.NextFrame when DeathCount >= MaxDeaths.
    /// </summary>
    public HookResult HandlePlayerDeathPre(EventPlayerDeath @event)
    {
        var victim = @event.Userid;
        if (victim == null || !victim.IsValid)
            return HookResult.Continue;

        var weapons = victim.PlayerPawn.Value?.WeaponServices?.MyWeapons;
        if (weapons == null)
            return HookResult.Continue;

        foreach (var handle in weapons)
        {
            var weapon = handle.Value;
            if (weapon == null || !weapon.IsValid)
                continue;

            uint weaponIndex = weapon.Index;
            if (!_moddedWeapons.TryGetValue(weaponIndex, out var modData))
                continue;

            modData.DeathCount++;
            string weaponName = weapon.DesignerName;

            if (modData.DeathCount >= modData.MaxDeaths)
            {
                _moddedWeapons.Remove(weaponIndex);

                Server.NextFrame(() =>
                {
                    var expiredWeapon = Utilities.GetEntityFromIndex<CBasePlayerWeapon>((int)weaponIndex);
                    if (expiredWeapon != null && expiredWeapon.IsValid)
                    {
                        expiredWeapon.Remove();
                    }
                });

                if (!victim.IsBot)
                {
                    victim.PrintToChat(
                        $" {ChatColors.Red}[Modded Weapon]{ChatColors.Default} Your upgraded {ChatColors.Yellow}{weaponName}{ChatColors.Default} " +
                        $"reached {ChatColors.LightRed}{modData.DeathCount}/{modData.MaxDeaths} deaths{ChatColors.Default} and shattered!"
                    );
                }
            }
            else if (!victim.IsBot)
            {
                victim.PrintToChat(
                    $" {ChatColors.Gold}[Modded Weapon]{ChatColors.Default} Your upgraded {ChatColors.Yellow}{weaponName}{ChatColors.Default} " +
                    $"was dropped ({ChatColors.LightRed}{modData.RemainingDeaths}/{modData.MaxDeaths} deaths remaining{ChatColors.Default})."
                );
            }
        }

        return HookResult.Continue;
    }

    public void OnEntityDeleted(CEntityInstance entity)
    {
        if (entity != null)
        {
            _moddedWeapons.Remove(entity.Index);
        }
    }

    public void ClearAll()
    {
        _moddedWeapons.Clear();
    }
}

