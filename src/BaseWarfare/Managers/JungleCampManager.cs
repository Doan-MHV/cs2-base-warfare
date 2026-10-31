using System;
using System.Collections.Generic;
using System.Drawing;
using System.Numerics;
using BaseWarfare.Models;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace BaseWarfare.Managers;

/// <summary>
/// Manages Zero-Spike prop_dynamic_override Neutral Jungle Creeps with:
///   1. Zero-Allocation 11-step HealthBarLut string lookup table.
///   2. Quantized 10%-bucket & 3-tier color network dirty-checking.
///   3. Entity pooling (CPointWorldText + CBeam) hidden via EF_NODRAW (32) when idle.
///   4. Active-only _aggroedCreeps combat loop (0.45s) and 0.15s per-player HUD rate limiting.
/// </summary>
public class JungleCampManager
{
    public const string CreepModelPath = "agents/models/tm_phoenix/tm_phoenix.vmdl";
    private const uint EF_NODRAW = 32; // Source 2 entity bitflag to hide an entity from rendering/networking

    // 1. ZERO-ALLOCATION STRING LOOKUP TABLE (Built once on plugin load)
    private static readonly string[] HealthBarLut = new string[11]
    {
        "░░░░░░░░░░",
        "█░░░░░░░░░",
        "██░░░░░░░░",
        "███░░░░░░░",
        "████░░░░░░",
        "█████░░░░░",
        "██████░░░░",
        "███████░░░",
        "████████░░",
        "█████████░",
        "██████████"
    };

    private readonly BasePlugin _plugin;
    private readonly EconomyAndBuffManager _economy;

    private readonly Dictionary<uint, OptimizedCreepData> _allCreeps = new();

    // 2. ACTIVE-ONLY SET: The 0.45s combat timer ONLY loops creeps inside this set!
    private readonly HashSet<uint> _aggroedCreeps = new();

    // 3. PER-PLAYER HUD THROTTLE: Prevents shotgun/SMG sprays from spamming Panorama UI
    private readonly float[] _lastHudUpdateTime = new float[65];

    private Timer? _combatTimer;
    private bool _isHooked;

    public JungleCampManager(BasePlugin plugin, EconomyAndBuffManager economy)
    {
        _plugin = plugin;
        _economy = economy;
    }

    public void Initialize()
    {
        if (!_isHooked)
        {
#pragma warning disable CS0618
            VirtualFunctions.CBaseEntity_TakeDamageOldFunc.Hook(OnEntityTakeDamage, HookMode.Pre);
#pragma warning restore CS0618
            _isHooked = true;
        }

        StartCombatTimer();
    }

    public void Shutdown()
    {
        if (_isHooked)
        {
#pragma warning disable CS0618
            VirtualFunctions.CBaseEntity_TakeDamageOldFunc.Unhook(OnEntityTakeDamage, HookMode.Pre);
#pragma warning restore CS0618
            _isHooked = false;
        }

        _combatTimer?.Kill();
        _combatTimer = null;
        CleanupAllCreeps();
    }

    public void StartCombatTimer()
    {
        _combatTimer?.Kill();
        _combatTimer = _plugin.AddTimer(0.45f, ProcessAggroedCreepsOnly, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
    }

    public void OnServerPrecacheResources(ResourceManifest manifest)
    {
        manifest.AddResource(CreepModelPath);
        manifest.AddResource("characters/models/tm_phoenix/tm_phoenix.vmdl");
    }

    // =========================================================================
    // SPAWN & POOL SETUP (Called only once when camp spawns)
    // =========================================================================

    public void SpawnOptimizedCreep(Vector position, QAngle rotation)
    {
        var prop = Utilities.CreateEntityByName<CDynamicProp>("prop_dynamic_override");
        if (prop == null) return;

        prop.Teleport(position, rotation, new Vector(0, 0, 0));

        using var kv = new CEntityKeyValues();
        kv.SetString("model", CreepModelPath);
        kv.SetInt("solid", (int)SolidType_t.SOLID_VPHYSICS);
        kv.SetInt("health", 300);
        prop.DispatchSpawn(kv);

        prop.TakesDamage = true;
        prop.Health = 300;
        prop.MaxHealth = 300;
        prop.UseHitboxesForRenderBox = true;

        if (prop.Collision != null)
        {
            prop.Collision.SolidType = SolidType_t.SOLID_VPHYSICS;
            prop.Collision.CollisionGroup = (byte)CollisionGroup.COLLISION_GROUP_INTERACTIVE_DEBRIS;
            Utilities.SetStateChanged(prop, "CBaseModelEntity", "m_Collision");
        }

        var creepData = new OptimizedCreepData
        {
            SpawnPosition = new Vector(position.X, position.Y, position.Z),
            SpawnRotation = new QAngle(rotation.X, rotation.Y, rotation.Z)
        };

        // Pre-allocate the 3D HP Bar (Starts hidden with EF_NODRAW & Enabled=false until provoked!)
        var worldText = Utilities.CreateEntityByName<CPointWorldText>("point_worldtext");
        if (worldText != null)
        {
            worldText.Teleport(
                new Vector(position.X, position.Y, position.Z + 82.0f),
                new QAngle(0, rotation.Y + 90.0f, 90.0f),
                new Vector(0, 0, 0)
            );
            worldText.Enabled = false;
            worldText.FontSize = 38.0f;
            worldText.WorldUnitsPerPx = 0.22f;
            worldText.JustifyHorizontal = PointWorldTextJustifyHorizontal_t.POINT_WORLD_TEXT_JUSTIFY_HORIZONTAL_CENTER;
            worldText.DrawBackground = true;
            worldText.MessageText = HealthBarLut[10];
            worldText.Color = Color.FromArgb(255, 76, 217, 100);
            worldText.DispatchSpawn();
            worldText.AcceptInput("SetParent", prop, prop, "!activator");

            worldText.Effects |= EF_NODRAW; // Hidden while unprovoked -> Zero client rendering cost!
            Utilities.SetStateChanged(worldText, "CBaseEntity", "m_fEffects");
            Utilities.SetStateChanged(worldText, "CPointWorldText", "m_bEnabled");

            creepData.HealthBarEntity = worldText;
        }

        // Pre-allocate ONE reusable tracer beam for this creep
        var beam = Utilities.CreateEntityByName<CBeam>("env_beam");
        if (beam != null)
        {
            Vector hiddenPos = new(position.X, position.Y, position.Z - 2000.0f);
            beam.Render = Color.FromArgb(0, 0, 0, 0);
            beam.Width = 0.0f;
            beam.EndWidth = 0.0f;
            beam.Teleport(hiddenPos, new QAngle(0, 0, 0), new Vector(0, 0, 0));
            beam.EndPos.X = hiddenPos.X;
            beam.EndPos.Y = hiddenPos.Y;
            beam.EndPos.Z = hiddenPos.Z;
            beam.DispatchSpawn();

            Utilities.SetStateChanged(beam, "CBeam", "m_vecEndPos");
            Utilities.SetStateChanged(beam, "CBeam", "m_fWidth");
            Utilities.SetStateChanged(beam, "CBeam", "m_fEndWidth");
            Utilities.SetStateChanged(beam, "CBaseModelEntity", "m_clrRender");

            // Apply EF_NODRAW after initial collapsed state is networked to clients
            _plugin.AddTimer(0.05f, () =>
            {
                if (beam != null && beam.IsValid && beam.Width == 0.0f)
                {
                    beam.Effects |= EF_NODRAW;
                    Utilities.SetStateChanged(beam, "CBaseEntity", "m_fEffects");
                }
            });

            creepData.PooledTracerBeam = beam;
        }

        _allCreeps[prop.Index] = creepData;
    }

    /// <summary>
    /// Spawns a neutral jungle camp 120 units in front of the player (callable via css_spawncamp or UI).
    /// </summary>
    public bool SpawnCampInFrontOfPlayer(CCSPlayerController? player)
    {
        if (player == null || !player.IsValid || !player.PawnIsAlive)
            return false;

        var pawn = player.PlayerPawn.Value;
        if (pawn?.AbsOrigin == null)
            return false;

        QAngle eyeAngles = pawn.EyeAngles;
        float yawRad = eyeAngles.Y * (MathF.PI / 180.0f);
        Vector spawnPos = new(
            pawn.AbsOrigin.X + MathF.Cos(yawRad) * 120.0f,
            pawn.AbsOrigin.Y + MathF.Sin(yawRad) * 120.0f,
            pawn.AbsOrigin.Z
        );
        QAngle spawnRot = new(0f, eyeAngles.Y + 180.0f, 0f);

        SpawnOptimizedCreep(spawnPos, spawnRot);
        player.PrintToChat($" {ChatColors.Gold}[Jungle]{ChatColors.Default} Spawned a Neutral Jungle Creep camp (300 HP | +$250 Bounty | 30s Respawn)!");
        return true;
    }

    // =========================================================================
    // QUANTIZED, DIRTY-CHECKED HP BAR UPDATE (95% Fewer Network Packets)
    // =========================================================================

    private void UpdateHealthBarIfChanged(OptimizedCreepData creep)
    {
        var bar = creep.HealthBarEntity;
        if (bar == null || !bar.IsValid) return;

        // Unhide the bar if this is the first hit
        if ((bar.Effects & EF_NODRAW) != 0 || !bar.Enabled)
        {
            bar.Effects &= ~EF_NODRAW;
            bar.Enabled = true;
            Utilities.SetStateChanged(bar, "CBaseEntity", "m_fEffects");
            Utilities.SetStateChanged(bar, "CPointWorldText", "m_bEnabled");
        }

        float ratio = Math.Clamp((float)creep.Health / creep.MaxHealth, 0.0f, 1.0f);
        int currentSegment = (int)Math.Ceiling(ratio * 10);

        // ONLY send a network packet if the 10% block count actually dropped!
        if (currentSegment != creep.LastDisplayedSegment)
        {
            creep.LastDisplayedSegment = currentSegment;
            bar.MessageText = HealthBarLut[currentSegment]; // Zero string allocation!
            Utilities.SetStateChanged(bar, "CPointWorldText", "m_messageText");
        }

        // ONLY send a color change packet when crossing 60% or 30% thresholds (Max 2 times per creep life!)
        int colorTier = ratio > 0.6f ? 2 : (ratio > 0.3f ? 1 : 0);
        if (colorTier != creep.LastColorTier)
        {
            creep.LastColorTier = colorTier;
            bar.Color = colorTier == 2
                ? Color.FromArgb(255, 76, 217, 100)
                : (colorTier == 1 ? Color.FromArgb(255, 255, 204, 0) : Color.FromArgb(255, 255, 59, 48));

            Utilities.SetStateChanged(bar, "CPointWorldText", "m_Color");
        }
    }

    // =========================================================================
    // DAMAGE HANDLER WITH HUD RATE-LIMITING (TakeDamage Hook + Ray-Cylinder Hit)
    // =========================================================================

    private HookResult OnEntityTakeDamage(DynamicHook hook)
    {
        var victimEntity = hook.GetParam<CEntityInstance>(0);
        if (victimEntity == null || !_allCreeps.TryGetValue(victimEntity.Index, out var creep))
            return HookResult.Continue;

        var damageInfo = hook.GetParam<CTakeDamageInfo>(1);
        var attackerPawn = damageInfo.Attacker.Value?.As<CCSPlayerPawn>();
        var attackerController = attackerPawn?.OriginalController.Value;

        if (attackerController == null || !attackerController.IsValid || attackerPawn?.AbsOrigin == null)
            return HookResult.Continue;

        ApplyCreepDamage(victimEntity.Index, creep, attackerController, attackerPawn.AbsOrigin, (int)damageInfo.Damage);
        return HookResult.Handled;
    }

    /// <summary>
    /// Zero-allocation ray-cylinder hit detection for character models on prop_dynamic_override
    /// whose SurroundingMins/Maxs are (0,0,0) in CS2's server trace pass.
    /// </summary>
    public HookResult HandleBulletImpact(EventBulletImpact @event)
    {
        if (_allCreeps.Count == 0)
            return HookResult.Continue;

        var attacker = @event.Userid;
        if (attacker == null || !attacker.IsValid || !attacker.PawnIsAlive)
            return HookResult.Continue;

        var pawn = attacker.PlayerPawn.Value;
        if (pawn?.AbsOrigin == null)
            return HookResult.Continue;

        Vector3 rayStart = new(
            pawn.AbsOrigin.X,
            pawn.AbsOrigin.Y,
            pawn.AbsOrigin.Z + (pawn.ViewOffset?.Z ?? 64.0f)
        );
        Vector3 rayEnd = new(@event.X, @event.Y, @event.Z);

        float dx = rayEnd.X - rayStart.X;
        float dy = rayEnd.Y - rayStart.Y;
        float lenSq2D = dx * dx + dy * dy;
        if (lenSq2D < 1.0f)
            return HookResult.Continue;

        uint hitPropIndex = 0;
        OptimizedCreepData? hitCreep = null;
        float closestT = float.MaxValue;
        bool isHeadshot = false;

        foreach (var (propIndex, creep) in _allCreeps)
        {
            float cx = creep.SpawnPosition.X;
            float cy = creep.SpawnPosition.Y;

            float t = ((cx - rayStart.X) * dx + (cy - rayStart.Y) * dy) / lenSq2D;
            if (t < 0.0f || t > 1.0f || t >= closestT)
                continue;

            float px = rayStart.X + t * dx;
            float py = rayStart.Y + t * dy;
            float distSqXY = (px - cx) * (px - cx) + (py - cy) * (py - cy);

            // 26-unit horizontal cylinder radius around the standing creep
            if (distSqXY > 26.0f * 26.0f)
                continue;

            float pz = rayStart.Z + t * (rayEnd.Z - rayStart.Z);
            float minZ = creep.SpawnPosition.Z - 4.0f;
            float maxZ = creep.SpawnPosition.Z + 78.0f;

            if (pz >= minZ && pz <= maxZ)
            {
                closestT = t;
                hitPropIndex = propIndex;
                hitCreep = creep;
                isHeadshot = pz >= (creep.SpawnPosition.Z + 56.0f);
            }
        }

        if (hitCreep != null)
        {
            var activeWeapon = pawn.WeaponServices?.ActiveWeapon.Value?.As<CCSWeaponBase>();
            int baseDamage = activeWeapon?.VData?.Damage ?? 32;
            int finalDamage = isHeadshot ? (int)(baseDamage * 1.5f) : baseDamage;

            ApplyCreepDamage(hitPropIndex, hitCreep, attacker, pawn.AbsOrigin, Math.Max(1, finalDamage));
        }

        return HookResult.Continue;
    }

    private void ApplyCreepDamage(
        uint propIndex,
        OptimizedCreepData creep,
        CCSPlayerController attackerController,
        Vector attackerOrigin,
        int damage)
    {
        float distSq = GetDistanceSquared(creep.SpawnPosition, attackerOrigin);
        if (distSq > creep.CampLeashRadiusSq)
            return;

        // Add to active combat set (O(1) HashSet operation)
        _aggroedCreeps.Add(propIndex);
        creep.Health -= damage;

        // 1. Update 3D Bar (Only networks if a 10% bucket changed)
        UpdateHealthBarIfChanged(creep);

        // 2. Throttle 2D Center HUD to max 1 update per 0.15s per player (Protects against shotgun/SMG spam)
        int slot = attackerController.Slot;
        float now = Server.CurrentTime;
        if (slot >= 0 && slot < _lastHudUpdateTime.Length &&
            (now - _lastHudUpdateTime[slot] >= 0.15f || creep.Health <= 0))
        {
            _lastHudUpdateTime[slot] = now;
            attackerController.PrintToCenter($"Jungle Creep: {Math.Max(0, creep.Health)} / {creep.MaxHealth} HP");
        }

        if (creep.Health <= 0)
        {
            CleanupAndScheduleRespawn(propIndex, creep, attackerController);
        }
    }

    // =========================================================================
    // ACTIVE-ONLY COMBAT LOOP (Skips all unprovoked camps completely)
    // =========================================================================

    private void ProcessAggroedCreepsOnly()
    {
        // If no camps are currently fighting, this timer exits in 1 nanosecond!
        if (_aggroedCreeps.Count == 0) return;

        var allPlayers = Utilities.GetPlayers();
        List<uint>? toDeaggro = null;

        foreach (uint propIndex in _aggroedCreeps)
        {
            if (!_allCreeps.TryGetValue(propIndex, out var creep)) continue;

            var prop = Utilities.GetEntityFromIndex<CDynamicProp>((int)propIndex);
            if (prop == null || !prop.IsValid) continue;

            CCSPlayerController? targetPlayer = null;
            float closestDistSq = creep.CampLeashRadiusSq;

            foreach (var player in allPlayers)
            {
                if (player == null || !player.IsValid || !player.PawnIsAlive || player.IsBot) continue;
                var pawn = player.PlayerPawn.Value;
                if (pawn?.AbsOrigin == null) continue;

                float distSq = GetDistanceSquared(creep.SpawnPosition, pawn.AbsOrigin);
                if (distSq < closestDistSq)
                {
                    closestDistSq = distSq;
                    targetPlayer = player;
                }
            }

            // No player in range -> Put camp back to sleep and hide its 3D UI
            if (targetPlayer == null)
            {
                toDeaggro ??= new List<uint>();
                toDeaggro.Add(propIndex);

                creep.Health = creep.MaxHealth;
                creep.LastDisplayedSegment = 10;
                creep.LastColorTier = 2;

                if (creep.HealthBarEntity != null && creep.HealthBarEntity.IsValid)
                {
                    var bar = creep.HealthBarEntity;
                    bar.MessageText = HealthBarLut[10];
                    bar.Color = Color.FromArgb(255, 76, 217, 100);
                    bar.Enabled = false;
                    Utilities.SetStateChanged(bar, "CPointWorldText", "m_messageText");
                    Utilities.SetStateChanged(bar, "CPointWorldText", "m_Color");
                    Utilities.SetStateChanged(bar, "CPointWorldText", "m_bEnabled");

                    _plugin.AddTimer(0.05f, () =>
                    {
                        if (bar != null && bar.IsValid && !bar.Enabled)
                        {
                            bar.Effects |= EF_NODRAW;
                            Utilities.SetStateChanged(bar, "CBaseEntity", "m_fEffects");
                        }
                    });
                }

                HidePooledBeam(creep);
                continue;
            }

            var targetPawn = targetPlayer.PlayerPawn.Value!;
            Vector targetOrigin = targetPawn.AbsOrigin!;

            // Aim and Fire using the POOLED tracer beam (Zero entity spawning!)
            float yaw = (float)(Math.Atan2(targetOrigin.Y - creep.SpawnPosition.Y, targetOrigin.X - creep.SpawnPosition.X) * (180.0 / Math.PI));
            prop.Teleport(null, new QAngle(0, yaw, 0), null);

            if (creep.PooledTracerBeam != null && creep.PooledTracerBeam.IsValid)
            {
                var beam = creep.PooledTracerBeam;
                beam.Effects &= ~EF_NODRAW; // Unhide pooled beam
                beam.Teleport(new Vector(creep.SpawnPosition.X, creep.SpawnPosition.Y, creep.SpawnPosition.Z + 55.0f), null, null);
                beam.Render = Color.FromArgb(255, 255, 190, 50);
                beam.Width = 2.0f;
                beam.EndWidth = 0.5f;
                beam.EndPos.X = targetOrigin.X;
                beam.EndPos.Y = targetOrigin.Y;
                beam.EndPos.Z = targetOrigin.Z + 45.0f;
                Utilities.SetStateChanged(beam, "CBaseEntity", "m_fEffects");
                Utilities.SetStateChanged(beam, "CBaseModelEntity", "m_clrRender");
                Utilities.SetStateChanged(beam, "CBeam", "m_fWidth");
                Utilities.SetStateChanged(beam, "CBeam", "m_fEndWidth");
                Utilities.SetStateChanged(beam, "CBeam", "m_vecEndPos");

                // Collapse pooled beam after 0.15s pulse so it fires crisp bursts and never sticks
                _plugin.AddTimer(0.15f, () => HidePooledBeam(creep));
            }

            int newHealth = targetPawn.Health - creep.AttackDamage;
            if (newHealth <= 0)
            {
                targetPawn.Health = 0;
                Utilities.SetStateChanged(targetPawn, "CBaseEntity", "m_iHealth");
                targetPawn.CommitSuicide(false, true);
                HidePooledBeam(creep);
                targetPlayer.PrintToChat($" {ChatColors.Red}[Jungle]{ChatColors.Default} You were slain by a Neutral Jungle Creep!");
            }
            else
            {
                targetPawn.Health = newHealth;
                Utilities.SetStateChanged(targetPawn, "CBaseEntity", "m_iHealth");
            }
        }

        if (toDeaggro != null)
        {
            foreach (uint id in toDeaggro) _aggroedCreeps.Remove(id);
        }
    }

    private void HidePooledBeam(OptimizedCreepData creep)
    {
        var beam = creep.PooledTracerBeam;
        if (beam == null || !beam.IsValid) return;

        // Step 1: Network the zero-width, zero-alpha, collapsed underground coordinates BEFORE setting EF_NODRAW.
        // (Setting EF_NODRAW in the same tick causes Source 2 CheckTransmit to skip sending the delta to clients,
        // leaving the client-side C_Beam scene object stuck at its previous EndPos!)
        Vector hiddenPos = new(creep.SpawnPosition.X, creep.SpawnPosition.Y, creep.SpawnPosition.Z - 2000.0f);
        beam.Effects &= ~EF_NODRAW;
        beam.Width = 0.0f;
        beam.EndWidth = 0.0f;
        beam.Render = Color.FromArgb(0, 0, 0, 0);
        beam.Teleport(hiddenPos, null, null);
        beam.EndPos.X = hiddenPos.X;
        beam.EndPos.Y = hiddenPos.Y;
        beam.EndPos.Z = hiddenPos.Z;

        Utilities.SetStateChanged(beam, "CBaseEntity", "m_fEffects");
        Utilities.SetStateChanged(beam, "CBeam", "m_fWidth");
        Utilities.SetStateChanged(beam, "CBeam", "m_fEndWidth");
        Utilities.SetStateChanged(beam, "CBaseModelEntity", "m_clrRender");
        Utilities.SetStateChanged(beam, "CBeam", "m_vecEndPos");

        // Step 2: Apply EF_NODRAW 50ms later once clients have received the collapsed state
        _plugin.AddTimer(0.05f, () =>
        {
            if (beam != null && beam.IsValid && beam.Width == 0.0f)
            {
                beam.Effects |= EF_NODRAW;
                Utilities.SetStateChanged(beam, "CBaseEntity", "m_fEffects");
            }
        });
    }

    private void CleanupAndScheduleRespawn(uint propIndex, OptimizedCreepData creep, CCSPlayerController killer)
    {
        _aggroedCreeps.Remove(propIndex);
        _allCreeps.Remove(propIndex);

        // Award CashReward directly to the killer's RPG & Shop economy!
        _economy.AddCash(killer, creep.CashReward);
        killer.PrintToChat(
            $" {ChatColors.Gold}[Jungle]{ChatColors.Default} +${creep.CashReward} Jungle Bounty! " +
            $"(Balance: {ChatColors.Green}${_economy.GetCash(killer):N0}{ChatColors.Default})"
        );

        var prop = Utilities.GetEntityFromIndex<CDynamicProp>((int)propIndex);
        var beam = creep.PooledTracerBeam;

        Server.NextFrame(() =>
        {
            if (beam != null && beam.IsValid) beam.Remove();
            if (prop != null && prop.IsValid) prop.Remove(); // Automatically removes parented HealthBarEntity
        });

        Vector pos = creep.SpawnPosition;
        QAngle rot = creep.SpawnRotation;
        _plugin.AddTimer(30.0f, () => SpawnOptimizedCreep(pos, rot), TimerFlags.STOP_ON_MAPCHANGE);
    }

    public void OnEntityDeleted(CEntityInstance entity)
    {
        if (entity != null)
        {
            _aggroedCreeps.Remove(entity.Index);
            _allCreeps.Remove(entity.Index);
        }
    }

    public void CleanupAllCreeps()
    {
        foreach (var (propIndex, creep) in _allCreeps)
        {
            if (creep.PooledTracerBeam != null && creep.PooledTracerBeam.IsValid)
                creep.PooledTracerBeam.Remove();

            var prop = Utilities.GetEntityFromIndex<CDynamicProp>((int)propIndex);
            if (prop != null && prop.IsValid)
                prop.Remove();
        }

        _aggroedCreeps.Clear();
        _allCreeps.Clear();
    }

    private static float GetDistanceSquared(Vector a, Vector b)
    {
        return Vector3.DistanceSquared(new Vector3(a.X, a.Y, a.Z), new Vector3(b.X, b.Y, b.Z));
    }
}
