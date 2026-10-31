using System;
using System.Collections.Generic;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace BaseWarfare.Models;

/// <summary>
/// Represents a purchasable item in the Base Warfare Shop (Weapon or Perk).
/// </summary>
public record ShopItem(
    int Id,
    string Name,
    string ShortCode,
    int Cost,
    bool IsWeapon,
    string? EntityName,
    string Description
);

/// <summary>
/// Holds all match scores, base capture progress, team spawn coordinates,
/// active player perks, and per-player screen menu state.
/// </summary>
public class MatchState
{
    public const int TargetKills = 100;
    public const float CaptureRadius = 450.0f;
    public const int MaxMoney = 30000;

    public int CtKills { get; set; } = 0;
    public int TKills { get; set; } = 0;

    /// <summary>CT progress (0-100%) toward capturing the Terrorist base.</summary>
    public float CtCapturePercent { get; set; } = 0f;

    /// <summary>T progress (0-100%) toward capturing the Counter-Terrorist base.</summary>
    public float TCapturePercent { get; set; } = 0f;

    public int CtsInTBase { get; set; } = 0;
    public int TsInCtBase { get; set; } = 0;
    public bool MatchEnded { get; set; } = false;

    public Vector? CtBaseCenter { get; set; } = null;
    public Vector? TBaseCenter { get; set; } = null;

    // Active per-life power-up tracking by player Slot
    public HashSet<int> SpeedPlayers { get; } = new();
    public HashSet<int> JuggernautPlayers { get; } = new();
    public HashSet<int> VampirePlayers { get; } = new();
    public HashSet<int> GravityPlayers { get; } = new();
    public Dictionary<int, int> KillsTowardStimPack { get; } = new();

    // Per-player interactive screen shop state
    public HashSet<int> ShopOpenPlayers { get; } = new();
    public Dictionary<int, int> SelectedShopIndex { get; } = new();
    public Dictionary<int, PlayerButtons> PreviousButtons { get; } = new();
    public Dictionary<int, double> LastPurchaseTime { get; } = new();

    public void Reset()
    {
        CtKills = 0;
        TKills = 0;
        CtCapturePercent = 0f;
        TCapturePercent = 0f;
        CtsInTBase = 0;
        TsInCtBase = 0;
        MatchEnded = false;

        SpeedPlayers.Clear();
        JuggernautPlayers.Clear();
        VampirePlayers.Clear();
        GravityPlayers.Clear();
        KillsTowardStimPack.Clear();
        ShopOpenPlayers.Clear();
    }

    public void ClearPlayerPowerUps(int slot)
    {
        SpeedPlayers.Remove(slot);
        JuggernautPlayers.Remove(slot);
        VampirePlayers.Remove(slot);
        GravityPlayers.Remove(slot);
        KillsTowardStimPack.Remove(slot);
    }

    /// <summary>
    /// Strictly checks whether the player is inside their team's native spawn buyzone brush
    /// (with mp_buy_anywhere 0 and never artificially forcing m_bInBuyZone = true).
    /// </summary>
    public bool IsPlayerInFriendlyBase(CCSPlayerController player)
    {
        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !player.PawnIsAlive || pawn.AbsOrigin == null)
            return false;

        return pawn.InBuyZone;
    }

    public static float Distance2D(Vector a, Vector b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
