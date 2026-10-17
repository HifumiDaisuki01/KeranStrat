using System.Text.Json;
using BotControllerApi;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using KeranStrat.Core;

namespace KeranStrat.Plugin;

public sealed partial class KeranStratPlugin
{
    private Pose? _probeTargetPose;
    private string _probeWeapon = "";
    private readonly List<string> _probeEvents = [];
    private int _probePhase, _probeDeadline, _probeAt, _probeShots, _probeDamage;
    private CCSPlayerController? _probeT, _probeCT;
    private Track? _probeTrack;
    private long _probeMovement;
    private readonly HashSet<int> _probeBaseline = [];
    private readonly Dictionary<int, nint> _probeOwned = [];
    private Dictionary<string, string>? _probeCvars;
    private static bool ProbeFirearm(string weapon)
    {
        string name = weapon.StartsWith("weapon_", StringComparison.Ordinal) ? weapon : "weapon_" + weapon;
        return PrimaryWeapons.Contains(name) || name is "weapon_glock" or "weapon_usp_silencer" or "weapon_hkp2000" or "weapon_deagle" or "weapon_revolver" or "weapon_p250" or "weapon_fiveseven" or "weapon_tec9" or "weapon_cz75a" or "weapon_elite";
    }

    private void RegisterDefenseProbe()
    {
        AddCommand("css_ksd_probe", "Console-only empty-server native combat handoff verification", StartDefenseProbe);
        RegisterEventHandler<EventWeaponFire>((e, _) =>
        {
            if (_probePhase == 4 && e.Userid?.Handle == _probeT?.Handle && _probeEvents.Count < 16) _probeEvents.Add("fire:" + e.Weapon);
            if (_probePhase == 4 && e.Userid?.Handle == _probeT?.Handle && ProbeFirearm(e.Weapon)) { _probeShots++; _probeWeapon = e.Weapon; }
            return HookResult.Continue;
        });
        RegisterEventHandler<EventPlayerHurt>((e, _) =>
        {
            if (_probePhase == 4 && e.Userid?.Handle == _probeCT?.Handle && _probeEvents.Count < 16) _probeEvents.Add($"hurt:{e.Weapon}:{e.DmgHealth}:attacker={e.Attacker?.Slot}");
            if (_probePhase == 4 && e.Attacker?.Handle == _probeT?.Handle && e.Userid?.Handle == _probeCT?.Handle && ProbeFirearm(e.Weapon))
                _probeDamage += e.DmgHealth;
            return HookResult.Continue;
        });
        RegisterEventHandler<EventPlayerDeath>((e, _) =>
        {
            if (_defenseTestPhase == 3 && e.Userid?.Handle == _defenseTestTarget?.Handle &&
                e.Attacker is { IsValid: true, IsBot: true } attacker && _defenseBots.Any(b => b.Handle == attacker.Handle && b.UserId == attacker.UserId))
                _defenseTestNativeKill = true;
            return HookResult.Continue;
        });
    }
    private void StartDefenseProbe(CCSPlayerController? player, CommandInfo cmd)
    {
        if (player != null || VersusLease || OtherHumans(0) || Server.MapName != "de_dust2" || Utilities.GetPlayers().Any(p => p.IsValid && p.IsBot) || !Config.DefenseEnabled || DefenseBusy || _state != "idle" || _busy || _modeChanging || _trainingMode != "Practice" || !Bind())
            throw new InvalidOperationException("Combat probe requires empty, idle Practice server console.");
        _probeCvars = new();
        foreach (var name in new[] { "bot_dont_shoot", "bot_difficulty", "bot_stop", "bot_zombie", "bot_quota" })
            _probeCvars[name] = ConVar.Find(name)?.StringValue ?? throw new InvalidOperationException("Missing " + name);
        _probeBaseline.Clear(); _probeOwned.Clear();
        foreach (var p in Utilities.GetPlayers()) if (p.UserId is int id) _probeBaseline.Add(id);
        _probeT = null; _probeCT = null; _probeShots = 0; _probeDamage = 0; _probeTargetPose = null; _probeWeapon = "";
        _probeEvents.Clear();
        _probePhase = 1; _state = "combat-probe"; _probeDeadline = Server.TickCount + 64 * 60;
        Server.ExecuteCommand("bot_dont_shoot 0"); Server.ExecuteCommand("bot_difficulty 3");
        Server.ExecuteCommand("bot_stop 0"); Server.ExecuteCommand("bot_zombie 0");
        Server.ExecuteCommand("bot_add_t"); Server.ExecuteCommand("bot_add_ct"); Server.ExecuteCommand("bot_quota 2");
        cmd.ReplyToCommand("[KeranStrat Defense] Native replay → AI combat probe started.");
    }
    private bool DefenseProbeTick()
    {
        if (_probePhase == 0) return false;
        try
        {
            if (OtherHumans(0)) throw new InvalidOperationException("Human joined; probe stopped.");
            if (_probeT is { IsValid: true }) DefenseBotFunds(_probeT);
            if (_probeCT is { IsValid: true }) DefenseBotFunds(_probeCT);
            if (Server.TickCount > _probeDeadline) throw new TimeoutException("Native AI did not damage CT within deadline.");
            if (_probePhase == 1)
            {
                foreach (var bot in Utilities.GetPlayers().Where(p => p.IsValid && p.IsBot && p.UserId is int id && !_probeBaseline.Contains(id)))
                {
                    _probeOwned[bot.Slot] = bot.Handle;
                    if (bot.TeamNum == 2) _probeT = bot; else if (bot.TeamNum == 3) _probeCT = bot;
                }
                if (_probeT == null || _probeCT == null) return true;
                if (!_probeT.PawnIsAlive) _probeT.Respawn();
                if (!_probeCT.PawnIsAlive) _probeCT.Respawn();
                if (!_probeT.PawnIsAlive || !_probeCT.PawnIsAlive) return true;
                GivePracticeKit(_probeT, "ak47");
                var rifle = Inventory.Capture(_probeT, 1);
                rifle.Equipment = rifle.Equipment.Where(e => e.ClassName == "weapon_ak47").ToArray();
                rifle.Ammo = []; Inventory.Restore(_probeT, rifle);
                DefenseBotFunds(_probeT); DefenseBotFunds(_probeCT);
                // Diagnostic-only Dust2 fixture, based on a verified open native spawn-area pair.
                // Random spawns can put the target behind props or below a ramp.
                Inventory.RestorePose(_probeT, new Pose(-1181, -754, 121, 0, 0, 0));
                _probeTargetPose = new Pose(-1053, -754, 121, 0, 180, 0);
                _api!.Lock(_probeT.Slot, LockKind.All);
                _probeTrack = Inventory.Capture(_probeT, 1);
                _api!.Lock(_probeCT.Slot, LockKind.All);
                _probeCT.RemoveWeapons(); _probeCT.PlayerPawn.Value!.Health = 1000;
                _api.SetReplayPawn(_probeT.Slot, _probeT.PlayerPawn.Value!.Handle);
                _api.StartRecord(_probeT.Slot);
                _probeMovement = _api.StartUsercmdMovement(_probeT.Slot, 0, 0);
                _probeAt = Server.TickCount + 64; _probePhase = 2;
            }
            else if (_probePhase == 2 && Server.TickCount >= _probeAt)
            {
                _api!.CancelUsercmdMovement(_probeT!.Slot, _probeMovement); _probeMovement = 0;
                _api.StopRecord(_probeT.Slot);
                var motion = _api.GetRecordedMotionExtended(_probeT.Slot);
                _probeTrack!.Ticks = motion.ticks; _probeTrack.Subticks = motion.subs; _probeTrack.Commands = motion.commands;
                if (motion.ticks.Length < 16) throw new InvalidOperationException("Probe recording empty.");
                _engine!.Prepare(_probeT, _probeTrack); if (!_engine.Start(_probeT.Slot)) throw new InvalidOperationException("Replay refused.");
                _probeAt = Server.TickCount + motion.ticks.Length; _probePhase = 3;
            }
            else if (_probePhase == 3 && Server.TickCount >= _probeAt)
            {
                var p = Inventory.Capture(_probeT!, 1).Initial;
                Inventory.RestorePose(_probeCT!, _probeTargetPose!);
                _probeCT!.PlayerPawn.Value!.Health = 1000;
                if (!_engine!.HandoffToCombatAI(_probeT!.Slot)) throw new InvalidOperationException("AI handoff not released.");
                _probePhase = 4;
                Log("Defense probe handed off T bot at " + JsonSerializer.Serialize(p));
            }
            else if (_probePhase == 4 && _probeDamage > 0 && _probeShots > 0)
            {
                WriteProbeReport("PASS_REAL_AI_DAMAGE"); StopDefenseProbe();
            }
        }
        catch (Exception e) { WriteProbeReport("FAIL: " + e.Message); StopDefenseProbe(); }
        return true;
    }
    private void WriteProbeReport(string result)
    {
        var report = new { Result = result, Shots = _probeShots, Damage = _probeDamage, Weapon = _probeWeapon, Events = _probeEvents.ToArray(), TargetHealth = _probeCT?.PlayerPawn.Value?.Health,
            AttackerPosition = _probeT?.PlayerPawn.Value?.AbsOrigin?.ToString(), TargetPosition = _probeCT?.PlayerPawn.Value?.AbsOrigin?.ToString(),
            Frames = _probeTrack?.Ticks.Length, ReplayStopped = _probeT == null || !_api!.IsReplaying(_probeT.Slot),
            AllLockReleased = _probeT == null || !_api!.IsLocked(_probeT.Slot, LockKind.All), TimeUtc = DateTimeOffset.UtcNow };
        File.WriteAllText(Path.Combine(_dataRoot, "defense-probe-report.json"), JsonSerializer.Serialize(report));
        Log("Defense probe: " + JsonSerializer.Serialize(report));
    }
    private void StopDefenseProbe()
    {
        if (_probePhase == 0) return;
        foreach (var (slot, handle) in _probeOwned)
        {
            var bot = NativeReplayEngine.Player(slot);
            if (bot is not { IsValid: true, IsBot: true } || bot.Handle != handle) continue;
            try { _api?.StopRecord(slot); _api?.StopReplay(slot); _api?.Unlock(slot, LockKind.All); _engine?.Stop(slot); } catch (Exception e) { Log(e.Message); }
            if (bot.UserId is int id) Server.ExecuteCommand("kickid " + id);
        }
        if (_probeCvars != null) foreach (var (key, value) in _probeCvars) Server.ExecuteCommand(key + " " + value);
        _probeOwned.Clear(); _probePhase = 0; _probeCvars = null; _state = "idle";
    }
}
