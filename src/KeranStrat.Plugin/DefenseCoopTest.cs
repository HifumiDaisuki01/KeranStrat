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
    private int _coopTestPhase, _coopDeadline, _coopCase, _coopSeenRounds, _coopActed, _coopRestoreAt;
    private bool _coopChecked, _coopStarted, _coopDisconnect, _coopOwnerDisconnect, _coopSlotReused;
    private bool _coopMatrixOnly;
    private readonly List<DefenseParticipant> _coopTargets = [];
    private readonly HashSet<uint> _coopBaseline = [];
    private readonly List<object> _coopResults = [];
    private DefenseStatistics? _coopOldStats;
    private Dictionary<string, string> _coopCvars = [];
    private Dictionary<string, string> _coopScopedSnapshot = [];
    private DefenseIdentity? _coopDisconnectedIdentity;

    private void StartDefenseCoopTest(CCSPlayerController? player, CommandInfo command)
    {
        if (player != null || VersusLease || OtherHumans(0) || Utilities.GetPlayers().Any(p => p.IsValid && p.IsBot) || DefenseLease ||
            _coopTestPhase != 0 || _defenseTestPhase != 0 || _state != "idle" || _busy || _modeChanging ||
            Server.MapName != "de_dust2" || _trainingMode != "Practice" || !Config.DefenseEnabled || !Bind())
            throw new InvalidOperationException("多人自测仅允许空服、Dust2 Practice 的服务端控制台。");
        _coopTestPhase = 1; _coopDeadline = Server.TickCount + DefenseTicks(360000);
        _coopMatrixOnly = command.ArgCount > 1 && command.GetArg(1) == "matrix";
        _coopCase = _coopMatrixOnly ? 12 : 0; _coopSeenRounds = 0; _coopActed = 0; _coopChecked = false; _coopStarted = false;
        _coopDisconnect = false; _coopOwnerDisconnect = false; _coopSlotReused = false;
        _coopTargets.Clear(); _coopResults.Clear(); _coopDisconnectedIdentity = null;
        _coopRestoreAt = 0;
        _coopOldStats = _defenseStats; _defenseStats = new();
        _coopCvars = new[] { "bot_dont_shoot", "bot_difficulty", "bot_stop", "bot_zombie", "bot_quota", "bot_quota_mode", "mp_respawn_immunitytime", "mp_respawn_on_death_ct", "mp_respawn_on_death_t" }
            .ToDictionary(k => k, k => ConVar.Find(k)!.StringValue);
        StartDefenseProbe(null, command);
    }
    private void CoopConfigure(int countCT, int countT, bool repeat, bool reuse = false)
    {
        var owner = DefenseResolve(_coopTargets[0]) ?? throw new InvalidOperationException("Missing controlled owner");
        _defenseSession = new DefenseSession(_coopTargets[0]);
        for (int i = 1; i < countCT; i++) _defenseSession.Join(_coopTargets[i]);
        _defenseOwner = owner.SteamID; _defenseOwnerSlot = owner.Slot;
        var recorded = _probeTrack ?? throw new InvalidOperationException("Missing native fixture");
        _defenseTactic = new Tactic { Name = "defense-coop-diagnostic", Creator = "server-defense-cooptest", Team = 2, Map = Server.MapName, GameVersion = _gameVersion, TickInterval = Server.TickInterval };
        foreach (int role in (countT == 3 ? new[] { 1, 3, 5 } : Enumerable.Range(1, countT)))
            _defenseTactic.Tracks[role] = new Track { Role = role, Initial = recorded.Initial, Equipment = recorded.Equipment, Ammo = recorded.Ammo,
                Health = 100, Armor = 100, Helmet = true, OffsetMs = (role - 1) * 125, Ticks = recorded.Ticks, Subticks = recorded.Subticks, Commands = recorded.Commands };
        _defenseSettings = new DefenseSettings { TacticId = _defenseTactic.Id, Map = Server.MapName, RoundSeconds = 10, Repeat = repeat, Difficulty = 4 };
        for (int i = 0; i < countCT; i++)
        {
            var p = DefenseResolve(_coopTargets[i]) ?? throw new InvalidOperationException("Missing controlled CT");
            if (!p.PawnIsAlive) p.Respawn();
            var position = Inventory.Capture(p, 1); position.Initial = _probeTargetPose! with { X = _probeTargetPose!.X + i * 48 };
            _defenseSettings.Defenders[_coopTargets[i].Identity.SteamId] = position;
        }
        if (reuse) BeginDefenseRound(); else StartDefense(owner);
        _coopScopedSnapshot = new(_defenseCvars!); _coopStarted = true; _coopActed = 0; _coopChecked = false;
        Log($"Coop configured case={_coopCase}, state={_defenseState}, members={_defenseSession.Members.Count}");
    }
    private static DefenseResult CoopExpected(int index) => index is 2 or 7 ? DefenseResult.Failure : index is 3 or 8 ? DefenseResult.Draw : index == 4 ? DefenseResult.Timeout : DefenseResult.Success;
    private void CoopKillT()
    {
        Log($"Coop diagnostic kill-T: case={_coopCase}, state={_defenseState}, bots={_defenseBots.Count}");
        foreach (var bot in _defenseBots)
            if (NativeReplayEngine.Player(bot.Slot) is { PawnIsAlive: true } p && p.Handle == bot.Handle && p.UserId == bot.UserId)
            { Log($"Coop diagnostic killing role={bot.Role}, slot={bot.Slot}"); p.CommitSuicide(false, true); }
    }
    private void DefenseCoopTestTick()
    {
        if (_coopTestPhase == 0) return;
        try
        {
            if (OtherHumans(0)) throw new InvalidOperationException("真人加入，多人诊断已停止。");
            if (Server.TickCount > _coopDeadline) throw new TimeoutException("Cooperative diagnostic timeout");
            foreach (var member in _coopTargets)
                if (DefenseResolve(member) is { PawnIsAlive: true } p)
                {
                    _api!.Lock(p.Slot, LockKind.All);
                    // Controlled keep-alive is diagnostic-only, never part of human gameplay.
                    if (_defenseState != "restoring" && (_defenseState != "preparing" || _coopChecked)) p.PlayerPawn.Value!.Health = 10000;
                }
            if (_coopTestPhase == 1 && _probePhase == 0)
            {
                if (_probeDamage <= 0 || _probeTrack == null) throw new InvalidOperationException("Native AI damage probe did not pass");
                _coopBaseline.Clear(); foreach (var p in Utilities.GetPlayers()) if (p.IsValid) _coopBaseline.Add(p.EntityHandle.Raw);
                for (int i = 0; i < 5; i++) Server.ExecuteCommand("bot_add_ct");
                Server.ExecuteCommand("bot_quota 5"); _coopTestPhase = 2; return;
            }
            if (_coopTestPhase == 2)
            {
                var targets = Utilities.GetPlayers().Where(p => p.IsValid && p.IsBot && p.TeamNum == 3 && p.UserId != null && !_coopBaseline.Contains(p.EntityHandle.Raw)).OrderBy(p => p.UserId).ToArray();
                if (targets.Length != 5 || targets.Any(p => !p.PawnIsAlive)) return;
                for (int i = 0; i < targets.Length; i++)
                { _api!.Lock(targets[i].Slot, LockKind.All); GivePracticeKit(targets[i], i % 2 == 0 ? "awp" : "m4a1_silencer"); _coopTargets.Add(DefenseCreateParticipant(targets[i])); }
                CoopConfigure(_coopMatrixOnly ? 5 : 2, 5, true); _coopTestPhase = 3; return;
            }
            if (_coopTestPhase != 3) return;
            if (_coopStarted && _defenseState == "idle" && _defenseStats.Rounds == _coopSeenRounds && !(_coopCase == 14 && _coopActed == 1))
                throw new InvalidOperationException($"Training stopped unexpectedly: {_defenseError}; stop={_defenseStopReason}; members={_defenseSession?.Members.Count}; round={_defenseSession?.Round.Count}");
            if (_coopCase == 14 && _coopActed == 1)
            {
                if (_defenseSession != null || _defenseState != "idle") return;
                if (_coopRestoreAt == 0) { _coopRestoreAt = Server.TickCount + 2; return; }
                if (Server.TickCount < _coopRestoreAt) return;
                _coopOwnerDisconnect = true;
                bool restored = _coopScopedSnapshot.All(kv => ConVar.Find(kv.Key)!.StringValue == kv.Value);
                if (!restored) throw new InvalidOperationException("ConVar restoration failed");
                var report = new { Result = "PASS_COOPERATIVE_SERVER_AUTOMATION", NativeDamage = _probeDamage, Cases = _coopResults,
                    TenAutomaticRounds = !_coopMatrixOnly, FriendDisconnected = _coopDisconnect, OwnerDisconnectedStopsSession = _coopOwnerDisconnect,
                    OldIdentityRejected = _coopDisconnect, ActualSlotReused = _coopSlotReused, CvarsRestored = restored, HumanClientTested = false, TimeUtc = DateTimeOffset.UtcNow };
                File.WriteAllText(Path.Combine(_dataRoot, _coopMatrixOnly ? "defense-cooptest-matrix-report.json" : "defense-cooptest-report.json"), JsonSerializer.Serialize(report));
                Log(JsonSerializer.Serialize(report)); EndDefenseCoopTest(); return;
            }
            if (_defenseStats.Rounds > _coopSeenRounds)
            {
                var expected = CoopExpected(_coopCase);
                if (_defenseStats.LastResult != expected) throw new InvalidOperationException($"Case {_coopCase}: expected {expected}, got {_defenseStats.LastResult}");
                _coopResults.Add(new { Case = _coopCase, CT = _defenseSession!.Round.Count, T = _defenseBots.Count, Result = expected.ToString(), IndependentPositionsAndLoadouts = _coopChecked });
                _coopSeenRounds = _defenseStats.Rounds; _coopCase++; _coopActed = 0; _coopChecked = false;
                if (_coopCase == 14)
                {
                    // Disconnect the owner while the completed round is still leased.
                    // Avoid a diagnostic CT BOT being quota-evicted between two fixtures.
                    var owner = DefenseResolve(_coopTargets[0]) ?? throw new InvalidOperationException("Owner missing before disconnect test");
                    _coopActed = 1;
                    Server.ExecuteCommand("kickid " + owner.UserId!.Value);
                    return;
                }
                if (_coopCase >= 10)
                    CoopConfigure(_coopCase == 10 ? 1 : _coopCase == 11 ? 3 : _coopCase == 12 ? 5 : 2, _coopCase == 11 ? 3 : 5, true, reuse: true);
                return;
            }
            if (!_coopStarted && _defenseState == "idle")
            {
                CoopConfigure(_coopCase == 10 ? 1 : _coopCase == 11 ? 3 : _coopCase == 12 ? 5 : 2, _coopCase == 11 ? 3 : 5, false); return;
            }
            if (_defenseState == "preparing" && !_coopChecked)
            {
                foreach (var member in _defenseSession!.Round.Values)
                {
                    var p = DefenseResolve(member) ?? throw new InvalidOperationException("CT identity lost while restoring");
                    var got = Inventory.Capture(p, 1); var want = _defenseSettings!.Defenders[member.Identity.SteamId];
                    if (Math.Abs(got.Initial.X - want.Initial.X) > 4 || Math.Abs(got.Initial.Y - want.Initial.Y) > 4 ||
                        !want.Equipment.OrderBy(e => e.ClassName).SequenceEqual(got.Equipment.OrderBy(e => e.ClassName)) ||
                        !got.Ammo.SequenceEqual(want.Ammo) || got.Health != 100 || got.Armor != want.Armor || got.Helmet != want.Helmet)
                        throw new InvalidOperationException(member.Name + " position/loadout restoration mismatch");
                }
                _coopChecked = true;
            }
            if (_defenseState != "running" || Server.TickCount < _defenseRoundStart + DefenseTicks(1200)) return;
            if (_coopCase == 14)
            {
                var owner = DefenseResolve(_coopTargets[0])!; Server.ExecuteCommand("kickid " + owner.UserId!.Value); _coopActed = 1; return;
            }
            if (_coopCase == 13)
            {
                if (_coopActed == 0)
                {
                    _coopDisconnectedIdentity = _coopTargets[1].Identity;
                    _coopBaseline.Clear(); foreach (var p in Utilities.GetPlayers()) if (p.IsValid) _coopBaseline.Add(p.EntityHandle.Raw);
                    int remaining = Utilities.GetPlayers().Count(p => p.IsValid && p.IsBot) - 1;
                    Server.ExecuteCommand("kickid " + _coopDisconnectedIdentity.UserId); Server.ExecuteCommand("bot_quota " + remaining); _coopActed = 1; return;
                }
                if (_coopActed == 1)
                {
                    if (_defenseSession!.Members.Count != 1 || DefenseResolve(_coopTargets[1]) != null) return;
                    if (!Utilities.GetPlayers().Any(p => p.IsValid && p.IsBot && p.TeamNum == 3 && !_coopBaseline.Contains(p.EntityHandle.Raw)))
                    { int quota = Utilities.GetPlayers().Count(p => p.IsValid && p.IsBot) + 1; Server.ExecuteCommand("bot_add_ct"); Server.ExecuteCommand("bot_quota " + quota); }
                    _coopActed = 2; return;
                }
                if (_coopActed == 2)
                {
                    var replacement = Utilities.GetPlayers().FirstOrDefault(p => p.IsValid && p.IsBot && p.TeamNum == 3 && p.UserId != null && !_coopBaseline.Contains(p.EntityHandle.Raw));
                    if (replacement == null) return;
                    if (!replacement.PawnIsAlive) { replacement.Respawn(); return; }
                    var newMember = DefenseCreateParticipant(replacement);
                    if (_defenseSession!.Contains(newMember.Identity) || DefenseResolve(_coopTargets[1]) != null) throw new InvalidOperationException("Disconnected identity reused");
                    _coopSlotReused = newMember.Identity.Slot == _coopDisconnectedIdentity!.Slot;
                    _api!.Lock(replacement.Slot, LockKind.All); GivePracticeKit(replacement, "m4a1_silencer"); _coopTargets[1] = newMember;
                    _coopDisconnect = true; CoopKillT(); _coopActed = 3; return;
                }
                return;
            }
            if (_coopActed == 0)
            {
                if (_coopCase is 0 or 5) DefenseResolve(_coopTargets[0])!.CommitSuicide(false, true);
                else if (_coopCase is 1 or 6) DefenseResolve(_coopTargets[1])!.CommitSuicide(false, true);
                else if (_coopCase is 2 or 7 or 3 or 8)
                    foreach (var member in _defenseSession!.Round.Values) DefenseResolve(member)!.CommitSuicide(false, true);
                if (_coopCase is 3 or 8 || _coopCase >= 9) CoopKillT();
                _coopActed = Server.TickCount; return;
            }
            if (_coopCase is 0 or 1 or 5 or 6 && Server.TickCount >= _coopActed + DefenseTicks(500))
            {
                if (_defenseSession!.Round.Values.Count(m => !m.Dead) != 1) throw new InvalidOperationException("One CT death incorrectly ended team defense");
                CoopKillT();
            }
        }
        catch (Exception e)
        {
            File.WriteAllText(Path.Combine(_dataRoot, _coopMatrixOnly ? "defense-cooptest-matrix-report.json" : "defense-cooptest-report.json"), JsonSerializer.Serialize(new { Result = "FAIL_COOPERATIVE_AUTOMATION", Case = _coopCase, Phase = _coopTestPhase, ActionStage = _coopActed, Error = e.ToString(), TimeUtc = DateTimeOffset.UtcNow }));
            Log("Cooperative diagnostic failed: " + e); EndDefenseCoopTest();
        }
    }
    private void EndDefenseCoopTest()
    {
        if (_coopTestPhase == 0) return;
        StopDefenseProbe(); CloseDefense("多人诊断结束");
        foreach (var member in _coopTargets)
            if (DefenseResolve(member) is { IsBot: true } p)
            { _api?.Unlock(p.Slot, LockKind.All); if (p.UserId is int id) Server.ExecuteCommand("kickid " + id); }
        foreach (var (name, value) in _coopCvars) Server.ExecuteCommand(name + " " + value);
        _coopTestPhase = 0; _coopTargets.Clear();
        if (_coopOldStats != null) _defenseStats = _coopOldStats;
        _coopOldStats = null;
    }
}
