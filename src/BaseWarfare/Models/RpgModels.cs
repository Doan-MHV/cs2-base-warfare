using System;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace BaseWarfare.Models;

/// <summary>
/// Player-bound RPG progression state keyed by ulong SteamID.
/// </summary>
public class PlayerRpgState
{
    public int Cash { get; set; } = 800;
    public int BonusHealth { get; set; } = 0;
}

/// <summary>
/// Weapon-bound upgrade & durability state keyed by uint entity Index.
/// </summary>
public class WeaponModData
{
    public float MagMultiplier { get; set; } = 1.30f;
    public int BaseMaxClip { get; set; } = 30;
    public int DeathCount { get; set; } = 0;
    public int MaxDeaths { get; set; } = 2;

    public int BoostedMaxClip => (int)Math.Floor(BaseMaxClip * MagMultiplier);
    public int RemainingDeaths => Math.Max(0, MaxDeaths - DeathCount);
}

/// <summary>
/// Neutral Jungle Creep state & pooled entity references keyed by prop_dynamic_override Index.
/// </summary>
public class OptimizedCreepData
{
    public int Health = 300;
    public int MaxHealth = 300;
    public int CashReward = 250;
    public int AttackDamage = 12;
    public float CampLeashRadiusSq = 500.0f * 500.0f; // Pre-squared to avoid math in loop

    // Dirty-flag tracker: Only network the 3D HP bar when this integer changes (0 to 10)
    public int LastDisplayedSegment = 10;
    public int LastColorTier = 2; // 2 = Green, 1 = Yellow, 0 = Red

    public Vector SpawnPosition = new(0, 0, 0);
    public QAngle SpawnRotation = new(0, 0, 0);

    public CPointWorldText? HealthBarEntity;
    public CBeam? PooledTracerBeam; // Reused forever—never created/destroyed mid-combat!
}

