using System;
using System.Collections.Generic;
using System.Linq;
using BaseWarfare.Models;
using BaseWarfare.UI;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace BaseWarfare.Managers;

/// <summary>
/// Manages Base Warfare game rules, server CVars, team base locations,
/// kill rewards, Stim Pack (weapon_healthshot) distribution, power-up enforcement,
/// base capture progress, and win conditions.
/// </summary>
public class GameModeManager
{
    private readonly BasePlugin _plugin;
    private readonly MatchState _state;
    private readonly ScreenHudManager _hud;
    private readonly EconomyAndBuffManager _economy;

    public GameModeManager(BasePlugin plugin, MatchState state, ScreenHudManager hud, EconomyAndBuffManager economy)
    {
        _plugin = plugin;
        _state = state;
        _hud = hud;
        _economy = economy;
    }

    public void ApplyGameModeCvars()
    {
        Server.ExecuteCommand("mp_warmup_end");

        // Respawn strictly at CT & T Home Bases (never random DM spawns)
        Server.ExecuteCommand("mp_respawn_on_death_ct 1");
        Server.ExecuteCommand("mp_respawn_on_death_t 1");
        Server.ExecuteCommand("mp_randomspawn 0");
        Server.ExecuteCommand("mp_randomspawn_los 0");
        Server.ExecuteCommand("mp_respawn_immunitytime 2");

        // Disable C4 bomb and defusers, drop all guns on death (mp_death_drop_gun 3 = both primary & secondary + G key enabled)
        Server.ExecuteCommand("mp_give_player_c4 0");
        Server.ExecuteCommand("mp_death_drop_c4 0");
        Server.ExecuteCommand("mp_defuser_allocation 0");
        Server.ExecuteCommand("mp_death_drop_defuser 0");
        Server.ExecuteCommand("mp_death_drop_gun 3");
        Server.ExecuteCommand("mp_death_drop_grenade 0");
        Server.ExecuteCommand("mp_death_drop_healthshot 0");

        // Prevent server auto-kick and enemy suicide compensation rewards when players are killed by Neutral Jungle Creeps
        Server.ExecuteCommand("mp_autokick 0");
        Server.ExecuteCommand("mp_suicide_penalty 0");
        Server.ExecuteCommand("mp_playercashawards 0");
        Server.ExecuteCommand("mp_teamcashawards 0");
        Server.ExecuteCommand("cash_player_killed_enemy_default 0");
        Server.ExecuteCommand("cash_player_killed_enemy_factor 0");

        // Enable Deathmatch Medi-Shot / Stim Pack (+50 HP smooth heal on pressing X)
        Server.ExecuteCommand("sv_health_approach_enabled 1");
        Server.ExecuteCommand("healthshot_health 50");
        Server.ExecuteCommand("healthshot_healthboost_time 1");
        Server.ExecuteCommand("sv_health_approach_speed 20");
        Server.ExecuteCommand("ammo_item_limit_healthshot 1");

        // Economy & match timer settings
        Server.ExecuteCommand("sv_buy_status_override 0");
        Server.ExecuteCommand("mp_buy_during_immunity 1");
        Server.ExecuteCommand("mp_buytime 99999");
        Server.ExecuteCommand("mp_buy_anywhere 0");
        Server.ExecuteCommand("mp_maxmoney 30000");
        Server.ExecuteCommand("mp_startmoney 800");
        Server.ExecuteCommand("mp_afterroundmoney 0");
        Server.ExecuteCommand("mp_roundtime 60");
        Server.ExecuteCommand("mp_roundtime_defuse 60");
        Server.ExecuteCommand("mp_roundtime_hostage 60");
        Server.ExecuteCommand("mp_ignore_round_win_conditions 1");
        Server.ExecuteCommand("mp_freezetime 2");

        DisableBombSitesAndLooseC4();
    }

    private static void DisableBombSitesAndLooseC4()
    {
        foreach (var bombTarget in Utilities.FindAllEntitiesByDesignerName<CBaseEntity>("func_bomb_target"))
        {
            if (bombTarget != null && bombTarget.IsValid)
                bombTarget.AcceptInput("Disable");
        }

        foreach (var c4 in Utilities.FindAllEntitiesByDesignerName<CBaseEntity>("weapon_c4"))
        {
            if (c4 != null && c4.IsValid)
                c4.Remove();
        }
    }

    /// <summary>
    /// Finds the true 5v5 competitive team spawn centers while ignoring scattered
    /// Deathmatch spawn points on maps like de_dust2 and de_mirage.
    /// </summary>
    public void LocateTeamBases()
    {
        var ctSpawns = Utilities.FindAllEntitiesByDesignerName<SpawnPoint>("info_player_counterterrorist")
            .Where(s => s.IsValid && s.AbsOrigin != null)
            .Select(s => s.AbsOrigin!)
            .ToList();
        _state.CtBaseCenter = FindTightClusterCenter(ctSpawns);

        var tSpawns = Utilities.FindAllEntitiesByDesignerName<SpawnPoint>("info_player_terrorist")
            .Where(s => s.IsValid && s.AbsOrigin != null)
            .Select(s => s.AbsOrigin!)
            .ToList();
        _state.TBaseCenter = FindTightClusterCenter(tSpawns);
    }

    private static Vector? FindTightClusterCenter(List<Vector> points)
    {
        if (points.Count == 0) return null;

        // Competitive 5v5 spawns are tightly grouped within 420 units of each other
        Vector bestSeed = points
            .OrderByDescending(p => points.Count(other => MatchState.Distance2D(p, other) <= 420f))
            .First();

        var cluster = points.Where(p => MatchState.Distance2D(bestSeed, p) <= 420f).ToList();
        return new Vector(
            cluster.Average(p => p.X),
            cluster.Average(p => p.Y),
            cluster.Average(p => p.Z)
        );
    }

    public void HandlePlayerSpawn(CCSPlayerController? player)
    {
        if (player == null || !player.IsValid) return;

        int slot = player.Slot;
        _state.ClearPlayerPowerUps(slot);
        _hud.RemovePlayerHud(slot);

        // Re-apply permanent RPG health buffs on Server.NextFrame
        _economy.ApplyPermanentBuffsOnSpawn(player);

        // Strip C4 if present and record base center (Stim Pack is earned every 2 kills)
        _plugin.AddTimer(0.2f, () =>
        {
            if (player == null || !player.IsValid || !player.PawnIsAlive || player.PlayerPawn.Value == null)
                return;

            var pawn = player.PlayerPawn.Value;

            StripPlayerC4(pawn);

            if (pawn.AbsOrigin == null || !pawn.InBuyZone)
                return;

            var pos = pawn.AbsOrigin;
            if (player.TeamNum == (byte)CsTeam.CounterTerrorist && _state.CtBaseCenter == null)
            {
                _state.CtBaseCenter = new Vector(pos.X, pos.Y, pos.Z);
            }
            else if (player.TeamNum == (byte)CsTeam.Terrorist && _state.TBaseCenter == null)
            {
                _state.TBaseCenter = new Vector(pos.X, pos.Y, pos.Z);
            }
        });
    }

    private static void StripPlayerC4(CCSPlayerPawn pawn)
    {
        var weapons = pawn.WeaponServices?.MyWeapons;
        if (weapons == null) return;

        foreach (var handle in weapons)
        {
            var weapon = handle.Value;
            if (weapon != null && weapon.IsValid && weapon.DesignerName == "weapon_c4")
            {
                weapon.Remove();
            }
        }
    }

    /// <summary>
    /// Gives the player a Deathmatch Medi-Shot Stim Pack (weapon_healthshot, key X)
    /// if they do not already have one in their inventory.
    /// </summary>
    public static bool GiveStimPackIfMissing(CCSPlayerController player, CCSPlayerPawn pawn)
    {
        var weapons = pawn.WeaponServices?.MyWeapons;
        if (weapons != null)
        {
            foreach (var handle in weapons)
            {
                var weapon = handle.Value;
                if (weapon != null && weapon.IsValid && weapon.DesignerName == "weapon_healthshot")
                {
                    return false;
                }
            }
        }

        player.GiveNamedItem("weapon_healthshot");
        return true;
    }

    public void HandlePlayerDeath(CCSPlayerController? attacker, CCSPlayerController? victim, bool isHeadshot)
    {
        if (_state.MatchEnded) return;

        if (victim != null && victim.IsValid)
        {
            _state.ClearPlayerPowerUps(victim.Slot);
            _state.ShopOpenPlayers.Remove(victim.Slot);
            _hud.RemovePlayerHud(victim.Slot);
        }

        if (attacker == null || !attacker.IsValid || victim == null || !victim.IsValid)
            return;

        if (attacker.Slot == victim.Slot || attacker.TeamNum == victim.TeamNum)
            return;

        // Increment team kill score
        if (attacker.TeamNum == (byte)CsTeam.CounterTerrorist)
            _state.CtKills++;
        else if (attacker.TeamNum == (byte)CsTeam.Terrorist)
            _state.TKills++;

        // Bonus kill money (+500 normal, +800 headshot) unified with EconomyAndBuffManager
        int bonusCash = isHeadshot ? 800 : 500;
        _economy.AddCash(attacker, bonusCash);

        // Award Stim Pack (weapon_healthshot) every 2 kills
        int stimKills = (_state.KillsTowardStimPack.TryGetValue(attacker.Slot, out int currentKills) ? currentKills : 0) + 1;
        bool grantedStim = false;

        if (stimKills >= 2 && attacker.PawnIsAlive && attacker.PlayerPawn.Value != null)
        {
            grantedStim = GiveStimPackIfMissing(attacker, attacker.PlayerPawn.Value);
            _state.KillsTowardStimPack[attacker.Slot] = grantedStim ? 0 : 2;
        }
        else
        {
            _state.KillsTowardStimPack[attacker.Slot] = stimKills;
        }

        if (!attacker.IsBot)
        {
            string hsText = isHeadshot ? " (HEADSHOT!)" : "";
            string stimText = grantedStim
                ? $" + {ChatColors.LightBlue}Stim Pack (2/2 Kills){ChatColors.Default}"
                : $" {ChatColors.Grey}(Stim Pack: {Math.Min(stimKills, 2)}/2 Kills){ChatColors.Default}";

            attacker.PrintToChat(
                $" {ChatColors.Green}[Base Warfare]{ChatColors.Default} +${bonusCash} Kill Reward{hsText}{stimText}!"
            );
        }

        // Vampire heal power-up (+35 HP per kill)
        if (_state.VampirePlayers.Contains(attacker.Slot) && attacker.PlayerPawn.Value != null)
        {
            var pawn = attacker.PlayerPawn.Value;
            int maxCap = EconomyAndBuffManager.DefaultBaseHealth + (_state.JuggernautPlayers.Contains(attacker.Slot) ? 225 : 100) + _economy.GetBonusHealth(attacker);
            pawn.Health = Math.Min(maxCap, pawn.Health + 35);
            Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");

            if (!attacker.IsBot)
                attacker.PrintToChat($" {ChatColors.Red}[Vampire]{ChatColors.Default} Drained +35 HP! (HP: {pawn.Health})");
        }

        CheckVictoryConditions();
    }

    public void OnHalfSecondTick()
    {
        if (_state.MatchEnded) return;

        if (_state.CtBaseCenter == null || _state.TBaseCenter == null)
        {
            LocateTeamBases();
        }

        var alivePlayers = Utilities.GetPlayers()
            .Where(p => p != null && p.IsValid && p.PawnIsAlive && p.PlayerPawn.Value?.AbsOrigin != null)
            .ToList();

        _state.CtsInTBase = 0;
        _state.TsInCtBase = 0;

        foreach (var p in alivePlayers)
        {
            var pawn = p.PlayerPawn.Value!;
            var pos = pawn.AbsOrigin!;

            // Enforce active Speed Boost & Moon Gravity continuously
            if (_state.SpeedPlayers.Contains(p.Slot) && pawn.VelocityModifier < 1.35f)
            {
                pawn.VelocityModifier = 1.35f;
                Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_flVelocityModifier");
            }

            if (_state.GravityPlayers.Contains(p.Slot))
            {
                pawn.GravityScale = 0.55f;
            }

            // Check if player is capturing the enemy base
            if (p.TeamNum == (byte)CsTeam.CounterTerrorist && _state.TBaseCenter != null)
            {
                if (MatchState.Distance2D(pos, _state.TBaseCenter) <= MatchState.CaptureRadius)
                    _state.CtsInTBase++;
            }
            else if (p.TeamNum == (byte)CsTeam.Terrorist && _state.CtBaseCenter != null)
            {
                if (MatchState.Distance2D(pos, _state.CtBaseCenter) <= MatchState.CaptureRadius)
                    _state.TsInCtBase++;
            }
        }

        // Update capture percentages (each tick is 0.5s: +2.5% per tick per attacker = 5%/sec)
        if (_state.CtsInTBase > 0)
            _state.CtCapturePercent = Math.Min(100f, _state.CtCapturePercent + (_state.CtsInTBase * 2.5f));
        else if (_state.CtCapturePercent > 0)
            _state.CtCapturePercent = Math.Max(0f, _state.CtCapturePercent - 0.5f);

        if (_state.TsInCtBase > 0)
            _state.TCapturePercent = Math.Min(100f, _state.TCapturePercent + (_state.TsInCtBase * 2.5f));
        else if (_state.TCapturePercent > 0)
            _state.TCapturePercent = Math.Max(0f, _state.TCapturePercent - 0.5f);

        CheckVictoryConditions();
    }

    public void CheckVictoryConditions()
    {
        if (_state.MatchEnded) return;

        if (_state.CtKills >= MatchState.TargetKills || _state.CtCapturePercent >= 100f)
        {
            _state.MatchEnded = true;
            string reason = _state.CtKills >= MatchState.TargetKills
                ? $"reached {MatchState.TargetKills} Kills!"
                : "captured the Terrorist Base (100%)!";

            Server.PrintToChatAll($" {ChatColors.Blue}========================================{ChatColors.Default}");
            Server.PrintToChatAll($" {ChatColors.Blue}COUNTER-TERRORISTS WIN!{ChatColors.Default} They {reason}");
            Server.PrintToChatAll($" {ChatColors.Blue}========================================{ChatColors.Default}");
            _plugin.AddTimer(5.0f, () => Server.ExecuteCommand("mp_restartgame 1"));
        }
        else if (_state.TKills >= MatchState.TargetKills || _state.TCapturePercent >= 100f)
        {
            _state.MatchEnded = true;
            string reason = _state.TKills >= MatchState.TargetKills
                ? $"reached {MatchState.TargetKills} Kills!"
                : "captured the Counter-Terrorist Base (100%)!";

            Server.PrintToChatAll($" {ChatColors.Gold}========================================{ChatColors.Default}");
            Server.PrintToChatAll($" {ChatColors.Gold}TERRORISTS WIN!{ChatColors.Default} They {reason}");
            Server.PrintToChatAll($" {ChatColors.Gold}========================================{ChatColors.Default}");
            _plugin.AddTimer(5.0f, () => Server.ExecuteCommand("mp_restartgame 1"));
        }
    }
}
