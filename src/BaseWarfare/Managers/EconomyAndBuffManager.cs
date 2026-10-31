using System;
using System.Collections.Generic;
using BaseWarfare.Models;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace BaseWarfare.Managers;

/// <summary>
/// Manages player-bound RPG economy (Cash) and permanent stats (BonusHealth)
/// keyed by ulong SteamID, synchronized with CS2's InGameMoneyServices.Account.
/// </summary>
public class EconomyAndBuffManager
{
    public const int DefaultBaseHealth = 1000;
    public const int DefaultHpUpgradeCost = 1500;
    public const int DefaultHpUpgradeAmount = 25;

    private readonly Dictionary<ulong, PlayerRpgState> _players = new();

    public PlayerRpgState GetOrCreateState(CCSPlayerController player)
    {
        ulong key = GetPlayerKey(player);
        if (!_players.TryGetValue(key, out var state))
        {
            int initialCash = player.InGameMoneyServices?.Account ?? 800;
            state = new PlayerRpgState { Cash = initialCash, BonusHealth = 0 };
            _players[key] = state;
        }
        else if (player.InGameMoneyServices != null && player.InGameMoneyServices.Account != state.Cash)
        {
            // Keep RPG cash synced if CS2 native economy or !cash modified Account
            state.Cash = player.InGameMoneyServices.Account;
        }

        return state;
    }

    public int GetCash(CCSPlayerController player)
    {
        return GetOrCreateState(player).Cash;
    }

    public int GetBonusHealth(CCSPlayerController player)
    {
        return GetOrCreateState(player).BonusHealth;
    }

    public void SetCash(CCSPlayerController player, int amount)
    {
        int clamped = Math.Clamp(amount, 0, MatchState.MaxMoney);
        var state = GetOrCreateState(player);
        state.Cash = clamped;

        if (player.InGameMoneyServices != null)
        {
            player.InGameMoneyServices.Account = clamped;
            Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInGameMoneyServices");
        }
    }

    public void AddCash(CCSPlayerController player, int amount)
    {
        if (player == null || !player.IsValid || amount == 0) return;

        int current = GetCash(player);
        SetCash(player, current + amount);
    }

    public bool TryDeductCash(CCSPlayerController player, int cost)
    {
        if (player == null || !player.IsValid) return false;

        int current = GetCash(player);
        if (current < cost)
            return false;

        SetCash(player, current - cost);
        return true;
    }

    /// <summary>
    /// Re-applies permanent BonusHealth buffs inside EventPlayerSpawn using Server.NextFrame
    /// and networks both m_iHealth and m_iMaxHealth.
    /// </summary>
    public void ApplyPermanentBuffsOnSpawn(CCSPlayerController? player)
    {
        if (player == null || !player.IsValid) return;

        Server.NextFrame(() =>
        {
            if (player == null || !player.IsValid || !player.PawnIsAlive)
                return;

            var pawn = player.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid)
                return;

            var state = GetOrCreateState(player);

            // Sync player cash display on spawn
            if (player.InGameMoneyServices != null && player.InGameMoneyServices.Account != state.Cash)
            {
                player.InGameMoneyServices.Account = state.Cash;
                Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInGameMoneyServices");
            }

            int targetMaxHealth = DefaultBaseHealth + state.BonusHealth;
            pawn.MaxHealth = targetMaxHealth;
            pawn.Health = targetMaxHealth;
            Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iMaxHealth");
            Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");
        });
    }

    /// <summary>
    /// Decoupled purchase method for permanent +HP upgrades (callable from css_buyhp and CCSCustomHudLayout).
    /// </summary>
    public bool TryBuyPermanentHealth(
        CCSPlayerController? player,
        int hpAmount = DefaultHpUpgradeAmount,
        int cost = DefaultHpUpgradeCost)
    {
        if (player == null || !player.IsValid || !player.PawnIsAlive)
            return false;

        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid)
            return false;

        int currentCash = GetCash(player);
        if (currentCash < cost)
        {
            player.PrintToChat(
                $" {ChatColors.Red}[RPG Shop]{ChatColors.Default} Need {ChatColors.Gold}${cost}{ChatColors.Default} " +
                $"for +{hpAmount} Permanent HP (You have ${currentCash})."
            );
            return false;
        }

        if (!TryDeductCash(player, cost))
            return false;

        var state = GetOrCreateState(player);
        state.BonusHealth += hpAmount;

        pawn.MaxHealth += hpAmount;
        pawn.Health += hpAmount;
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iMaxHealth");
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");

        player.PrintToChat(
            $" {ChatColors.Green}[RPG Shop]{ChatColors.Default} Purchased {ChatColors.LightBlue}+{hpAmount} Permanent HP{ChatColors.Default} (-${cost})! " +
            $"Total Permanent Bonus: {ChatColors.Gold}+{state.BonusHealth} HP{ChatColors.Default} (Current HP: {pawn.Health}/{pawn.MaxHealth})."
        );
        return true;
    }

    public void ResetAll()
    {
        _players.Clear();
    }

    private static ulong GetPlayerKey(CCSPlayerController player)
    {
        return player.SteamID != 0 ? player.SteamID : (ulong)(player.Slot + 1);
    }
}

