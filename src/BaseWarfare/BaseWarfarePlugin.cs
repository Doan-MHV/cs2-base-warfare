using BaseWarfare.Managers;
using BaseWarfare.Models;
using BaseWarfare.UI;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace BaseWarfare;

public class BaseWarfarePlugin : BasePlugin
{
    public override string ModuleName => "CS2 Base Warfare (KOTH RPG & Zero-Spike Jungle Creeps)";
    public override string ModuleVersion => "2.4.0";
    public override string ModuleAuthor => "Antigravity";
    public override string ModuleDescription => "Modular Base Warfare KOTH RPG mode with 2D Panorama custom_hud_layout, Permanent HP & Weapon Durability Perks, and Zero-Spike Jungle Creeps.";

    private MatchState _state = null!;
    private EconomyAndBuffManager _economy = null!;
    private WeaponPerkManager _weaponPerks = null!;
    private JungleCampManager _jungle = null!;
    private ShopManager _shop = null!;
    private ScreenHudManager _hud = null!;
    private GameModeManager _gameMode = null!;

    private CounterStrikeSharp.API.Modules.Timers.Timer? _gameLoopTimer;

    public override void Load(bool hotReload)
    {
        _state = new MatchState();
        _economy = new EconomyAndBuffManager();
        _weaponPerks = new WeaponPerkManager(this, _economy);
        _jungle = new JungleCampManager(this, _economy);
        _shop = new ShopManager(_state, _economy);
        _hud = new ScreenHudManager(_state, _shop, _economy, _weaponPerks, _jungle);
        _gameMode = new GameModeManager(this, _state, _hud, _economy);

        RegisterListener<Listeners.OnServerPrecacheResources>(OnServerPrecacheResources);
        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnTick>(OnServerTick);
        RegisterListener<Listeners.OnCustomHudClicked>(OnCustomHudClicked);
        RegisterListener<Listeners.OnEntityDeleted>(OnEntityDeleted);

        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeathPre, HookMode.Pre);
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeathPost, HookMode.Post);
        RegisterEventHandler<EventWeaponReload>(OnWeaponReload);
        RegisterEventHandler<EventItemPickup>(OnItemPickup);
        RegisterEventHandler<EventBulletImpact>(OnBulletImpact);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);

        _jungle.Initialize();
        RegisterCommands();
        StartLoopTimer();

        if (hotReload)
        {
            _gameMode.ApplyGameModeCvars();
            _gameMode.LocateTeamBases();
            _hud.EnsureHudEntity();

            foreach (var p in Utilities.GetPlayers())
            {
                if (p != null && p.IsValid && p.PawnIsAlive)
                    _economy.ApplyPermanentBuffsOnSpawn(p);
            }
        }
    }

    public override void Unload(bool hotReload)
    {
        RemoveListener<Listeners.OnServerPrecacheResources>(OnServerPrecacheResources);
        RemoveListener<Listeners.OnMapStart>(OnMapStart);
        RemoveListener<Listeners.OnTick>(OnServerTick);
        RemoveListener<Listeners.OnCustomHudClicked>(OnCustomHudClicked);
        RemoveListener<Listeners.OnEntityDeleted>(OnEntityDeleted);

        _gameLoopTimer?.Kill();
        _jungle?.Shutdown();
        _weaponPerks?.ClearAll();
        _hud?.CleanupAll();
    }

    private void RegisterCommands()
    {
        // Testing & General Shop commands
        AddCommand("css_cash", "Set money to $30,000 for testing", (p, _) => _shop.GrantMaxCash(p));
        AddCommand("css_shop", "Open/Close the 2D Panorama Base Warfare Shop", (p, _) => _shop.ToggleShop(p));
        AddCommand("css_buy", "Buy an item by ID (1-9) or short code", CommandBuy);
        AddCommand("css_bwreset", "Reset Base Warfare scores & apply cvars", CommandReset);

        // KOTH RPG Upgrades & Jungle Camp Commands (decoupled so UI callbacks can also invoke them)
        AddCommand("css_buyhp", "Buy +25 Permanent HP ($1,500)", (p, _) => _economy.TryBuyPermanentHealth(p));
        AddCommand("css_buymag", "Buy +30% Extended Mag with 2-death durability ($1,200)", (p, _) => _weaponPerks.TryBuyExtendedMag(p));
        AddCommand("css_spawncamp", "Spawn a Zero-Spike Neutral Jungle Creep camp in front of you", (p, _) => _jungle.SpawnCampInFrontOfPlayer(p));

        // Bot helper commands for easy solo testing (!bot, !botct, !bott, !kickbots)
        AddCommand("css_bot", "Add a bot to the enemy team", CommandAddEnemyBot);
        AddCommand("css_botct", "Add a Counter-Terrorist bot", (_, _) => Server.ExecuteCommand("bot_add_ct"));
        AddCommand("css_bott", "Add a Terrorist bot", (_, _) => Server.ExecuteCommand("bot_add_t"));
        AddCommand("css_kickbots", "Kick all bots", (_, _) => Server.ExecuteCommand("bot_kick"));

        // Numbered slot commands (!buy1 through !buy9)
        for (int i = 1; i <= 9; i++)
        {
            int itemId = i;
            AddCommand($"css_buy{itemId}", $"Buy Shop Item #{itemId}", (p, _) => _shop.TryBuyItemById(p, itemId));
        }

        // Weapon quick-buy aliases (!g1 through !g5)
        for (int i = 1; i <= 5; i++)
        {
            int weaponId = i;
            AddCommand($"css_g{weaponId}", $"Buy Weapon #{weaponId}", (p, _) => _shop.TryBuyItemById(p, weaponId));
        }

        // Direct Weapon & Perk alias commands (!ak, !m4, !awp, !deagle, !mp9, !speed, !jugg, !vamp, !grav)
        foreach (var item in _shop.Catalog)
        {
            string code = item.ShortCode;
            AddCommand($"css_{code}", $"Buy {item.Name} (${item.Cost})", (p, _) => _shop.TryBuyItemByCode(p, code));
        }
    }

    private void OnServerPrecacheResources(ResourceManifest manifest)
    {
        _jungle.OnServerPrecacheResources(manifest);
    }

    private void OnServerTick()
    {
        if (_state.MatchEnded) return;
        _hud.OnTick();
    }

    private void OnCustomHudClicked(CCSPlayerController player, CCSCustomHudLayout layout, string buttonId)
    {
        _hud.OnCustomHudClicked(player, layout, buttonId);
    }

    private void OnEntityDeleted(CEntityInstance entity)
    {
        _weaponPerks.OnEntityDeleted(entity);
        _jungle.OnEntityDeleted(entity);
    }

    private void OnMapStart(string mapName)
    {
        _hud.CleanupAll();
        _jungle.CleanupAllCreeps();
        _weaponPerks.ClearAll();
        _state.Reset();
        _state.CtBaseCenter = null;
        _state.TBaseCenter = null;

        _jungle.StartCombatTimer();

        AddTimer(2.0f, () =>
        {
            _gameMode.ApplyGameModeCvars();
            _gameMode.LocateTeamBases();
            _hud.EnsureHudEntity();
        });

        StartLoopTimer();
    }

    private void StartLoopTimer()
    {
        _gameLoopTimer?.Kill();
        _gameLoopTimer = AddTimer(0.5f, () => _gameMode.OnHalfSecondTick(), TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
    }

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        _hud.CleanupAll();
        _jungle.CleanupAllCreeps();
        _weaponPerks.ClearAll();
        _state.Reset();
        _gameMode.ApplyGameModeCvars();
        _gameMode.LocateTeamBases();
        _hud.EnsureHudEntity();

        Server.PrintToChatAll($" {ChatColors.Green}[Base Warfare]{ChatColors.Default} Match Started! Press {ChatColors.LightBlue}R{ChatColors.Default} (or {ChatColors.Yellow}!shop{ChatColors.Default}) in Home Base, or use {ChatColors.Gold}!buyhp{ChatColors.Default}, {ChatColors.Gold}!buymag{ChatColors.Default}, {ChatColors.Gold}!spawncamp{ChatColors.Default}!");
        Server.PrintToChatAll($" {ChatColors.Green}[Base Warfare]{ChatColors.Default} Win by reaching {ChatColors.Gold}{MatchState.TargetKills} Kills{ChatColors.Default} OR capturing the {ChatColors.Red}Enemy Base (100%){ChatColors.Default}!");
        return HookResult.Continue;
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        _gameMode.HandlePlayerSpawn(@event.Userid);
        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Pre)]
    public HookResult OnPlayerDeathPre(EventPlayerDeath @event, GameEventInfo info)
    {
        return _weaponPerks.HandlePlayerDeathPre(@event);
    }

    private HookResult OnPlayerDeathPost(EventPlayerDeath @event, GameEventInfo info)
    {
        _gameMode.HandlePlayerDeath(@event.Attacker, @event.Userid, @event.Headshot);
        return HookResult.Continue;
    }

    private HookResult OnWeaponReload(EventWeaponReload @event, GameEventInfo info)
    {
        return _weaponPerks.HandleWeaponReload(@event);
    }

    private HookResult OnItemPickup(EventItemPickup @event, GameEventInfo info)
    {
        return _weaponPerks.HandleItemPickup(@event);
    }

    private HookResult OnBulletImpact(EventBulletImpact @event, GameEventInfo info)
    {
        return _jungle.HandleBulletImpact(@event);
    }

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player != null)
        {
            _state.ClearPlayerPowerUps(player.Slot);
            _state.ShopOpenPlayers.Remove(player.Slot);
            _hud.RemovePlayerHud(player.Slot);
        }
        return HookResult.Continue;
    }

    private void CommandBuy(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid) return;

        if (info.ArgCount < 2)
        {
            int idx = _state.SelectedShopIndex.TryGetValue(player.Slot, out int s) ? s : 0;
            if (idx >= 0 && idx < _shop.Catalog.Count)
                _shop.TryBuyItem(player, _shop.Catalog[idx]);
            return;
        }

        string arg = info.GetArg(1).Trim();
        if (int.TryParse(arg, out int id))
        {
            if (!_shop.TryBuyItemById(player, id))
                player.PrintToChat($" {ChatColors.Red}[Base Shop]{ChatColors.Default} Unknown item ID '{id}'. Use 1-9 or press {ChatColors.Yellow}R{ChatColors.Default} in base.");
        }
        else
        {
            if (!_shop.TryBuyItemByCode(player, arg))
                player.PrintToChat($" {ChatColors.Red}[Base Shop]{ChatColors.Default} Unknown item '{arg}'. Press {ChatColors.Yellow}R{ChatColors.Default} in base to open the shop.");
        }
    }

    private void CommandReset(CCSPlayerController? player, CommandInfo info)
    {
        _hud.CleanupAll();
        _jungle.CleanupAllCreeps();
        _weaponPerks.ClearAll();
        _economy.ResetAll();
        _state.Reset();
        _gameMode.ApplyGameModeCvars();
        _gameMode.LocateTeamBases();
        _hud.EnsureHudEntity();
        Server.PrintToChatAll($" {ChatColors.Green}[Base Warfare]{ChatColors.Default} Scores, RPG Buffs, Jungle Creeps, 2D Panorama HUD, and CVars have been reset!");
    }

    private void CommandAddEnemyBot(CCSPlayerController? player, CommandInfo info)
    {
        Server.ExecuteCommand("bot_quota_mode normal");
        if (player != null && player.IsValid && player.TeamNum == (byte)CsTeam.CounterTerrorist)
        {
            Server.ExecuteCommand("bot_add_t");
            player.PrintToChat($" {ChatColors.Green}[Base Warfare]{ChatColors.Default} Spawned a Terrorist bot!");
        }
        else
        {
            Server.ExecuteCommand("bot_add_ct");
            player?.PrintToChat($" {ChatColors.Green}[Base Warfare]{ChatColors.Default} Spawned a Counter-Terrorist bot!");
        }
    }
}
