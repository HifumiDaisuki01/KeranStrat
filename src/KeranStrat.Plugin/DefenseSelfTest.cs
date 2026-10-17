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
    private int _defenseTestPhase, _defenseTestDeadline, _defenseTestRestoreAt;
    private CCSPlayerController? _defenseTestTarget;
    private readonly HashSet<int> _defenseTestBaseline = [];
    private Guid? _defenseTestTacticId;
    private bool _defenseTestKilledOne, _defenseTestIsolated;
    private object[] _defenseTestHandoffs = [];
    private DefenseStatistics? _defenseTestOldStats;
    private string _defenseTestOriginalShoot = "";
    private bool _defenseTestNativeKill;
    private bool _defenseTestCombatReady;

    private void StartDefenseSelfTest(CCSPlayerController? p, CommandInfo cmd)
    {
        if (p != null || VersusLease || OtherHumans(0) || Utilities.GetPlayers().Any(b => b.IsValid && b.IsBot) || DefenseLease || _defenseTestPhase != 0 || _coopTestPhase != 0 ||
            _state != "idle" || _busy || _modeChanging || Server.MapName != "de_dust2" || _trainingMode != "Practice" || !_practice.Active || !Config.DefenseEnabled || !Bind())
            throw new InvalidOperationException("Defense 自测仅空服服务端控制台可用。");
        _defenseTestPhase = 1; _defenseTestDeadline = Server.TickCount + DefenseTicks(180000);
        _defenseTestOriginalShoot = ConVar.Find("bot_dont_shoot")!.StringValue;
        _defenseTestOldStats = _defenseStats; _defenseStats = new DefenseStatistics();
        _defenseTestTarget = null; _defenseTestTacticId = null; _defenseTestIsolated = false; _defenseTestKilledOne = false;
        _defenseTestRestoreAt = 0;
        _defenseTestNativeKill = false;
        _defenseTestCombatReady = false;
        StartDefenseProbe(null, cmd);
    }
    private void DefenseSelfTestTick()
    {
        if (_defenseTestPhase == 0) return;
        try
        {
            if (OtherHumans(0)) throw new InvalidOperationException("真人加入，Defense 自测停止。");
            if (Server.TickCount > _defenseTestDeadline) throw new TimeoutException("Defense 自动循环自测超时。");
            if (_defenseTestTarget is { IsValid: true, PawnIsAlive: true } target)
            {
                _api!.Lock(target.Slot, LockKind.All);
                if (_defenseTestPhase >= 4) target.PlayerPawn.Value!.Health = 1000;
                if (_defenseTestPhase == 3 && !_defenseTestCombatReady)
                {
                    bool allCombat = _defenseState == "running" && _defenseController!.Entries.All(e => e.State == DefenseBotState.CombatAI);
                    target.PlayerPawn.Value!.Health = allCombat ? 100 : 1000;
                    _defenseTestCombatReady = allCombat;
                }
            }
            if (_defenseTestPhase == 1 && _probePhase == 0)
            {
                if (_probeDamage <= 0 || _probeTrack == null) throw new InvalidOperationException("原生交火验证未通过。");
                _defenseTestBaseline.Clear();
                foreach (var b in Utilities.GetPlayers()) if (b.UserId is int id) _defenseTestBaseline.Add(id);
                Server.ExecuteCommand("bot_add_ct"); Server.ExecuteCommand("bot_quota 1"); _defenseTestPhase = 2;
            }
            else if (_defenseTestPhase == 2)
            {
                var ct = Utilities.GetPlayers().FirstOrDefault(b => b.IsValid && b.IsBot && b.TeamNum == 3 && b.UserId is int id && !_defenseTestBaseline.Contains(id));
                if (ct == null) return;
                _defenseTestTarget = ct;
                if (!ct.PawnIsAlive) { ct.Respawn(); return; }
                _api!.Lock(ct.Slot, LockKind.All); GivePracticeKit(ct, "awp");
                var defender = Inventory.Capture(ct, 1);
                defender.Initial = _probeTargetPose!;
                Inventory.RestorePose(ct, defender.Initial);
                _defenseTactic = new Tactic { Name = "defense-selftest-" + Guid.NewGuid().ToString("N")[..8], Creator = "server-defense-selftest", Team = 2, Map = Server.MapName, GameVersion = _gameVersion, TickInterval = Server.TickInterval };
                var recorded = _probeTrack ?? throw new InvalidOperationException("Missing probe recording");
                foreach (int role in new[] { 1, 3, 5 })
                    _defenseTactic.Tracks[role] = new Track { Role = role, Initial = recorded.Initial, Equipment = recorded.Equipment, Ammo = recorded.Ammo,
                        Health = 100, Armor = 100, Helmet = true, OffsetMs = role == 1 ? 0 : role == 3 ? 125 : 250,
                        Ticks = recorded.Ticks, Subticks = recorded.Subticks, Commands = recorded.Commands };
                _defenseTestTacticId = _defenseTactic.Id;
                _store.Save(_defenseTactic); _defenseTactic = _store.Load(Server.MapName, _defenseTactic.Id.ToString("N"));
                _defenseSettings = new DefenseSettings { TacticId = _defenseTactic.Id, Map = Server.MapName, Defender = defender, Difficulty = 4, Repeat = true, RoundSeconds = 10,
                    HandoffMilliseconds = new() { [1] = 250, [3] = 500, [5] = null } };
                StartDefense(ct); _defenseTestPhase = 3;
            }
            else if (_defenseTestPhase == 3 && _defenseStats.Rounds == 1)
            {
                if (_defenseStats.LastResult != DefenseResult.Failure || !_defenseTestNativeKill) throw new InvalidOperationException("原生 AI 未产生预期的 CT 死亡失败判定。");
                _defenseTestHandoffs = _defenseController!.Entries.Select(e => (object)new { e.Role, e.StartedTick, e.HandoffTick, e.OffsetTicks, e.HandoffTicks, e.Automatic }).ToArray();
                if (_defenseController.Entries.Any(e => e.HandoffTick == null || (!e.Automatic && e.HandoffTick != e.StartedTick + e.HandoffTicks) ||
                    (e.Automatic && e.HandoffTick < e.StartedTick + e.HandoffTicks))) throw new InvalidOperationException("独立交接时间不正确。");
                _defenseTestPhase = 4;
            }
            else if (_defenseTestPhase == 4 && _defenseState == "running")
            {
                if (!_defenseTestKilledOne && Server.TickCount >= _defenseRoundStart + 2)
                {
                    var one = _defenseBots.First(b => b.Role == 1); NativeReplayEngine.Player(one.Slot)!.CommitSuicide(false, true); _defenseTestKilledOne = true;
                }
                else if (_defenseTestKilledOne)
                {
                    if (_defenseController!.DeadCount == 1 && _defenseController.Entries.Where(e => e.Role != 1).All(e => e.State != DefenseBotState.Dead)) _defenseTestIsolated = true;
                    if (_defenseController.Entries.Where(e => e.Role != 1).All(e => e.State == DefenseBotState.CombatAI))
                        foreach (var b in _defenseBots.Where(b => b.Role != 1)) NativeReplayEngine.Player(b.Slot)!.CommitSuicide(false, true);
                }
            }
            if (_defenseTestPhase == 4 && _defenseStats.Rounds == 2)
            {
                if (_defenseStats.LastResult != DefenseResult.Success || !_defenseTestIsolated) throw new InvalidOperationException("死亡隔离或防守成功判定失败。");
                _defenseTestPhase = 5;
            }
            if (_defenseTestPhase == 5)
            {
                if (_defenseStats.Rounds == 3)
                {
                    if (_defenseStats.LastResult != DefenseResult.Timeout) throw new InvalidOperationException("超时判定失败。");
                    _defenseSettings!.Repeat = false; _defenseTestPhase = 6;
                }
            }
            if (_defenseTestPhase == 6 && _defenseState == "idle")
            {
                if (_defenseTestRestoreAt == 0) { _defenseTestRestoreAt = Server.TickCount + 2; return; }
                if (Server.TickCount < _defenseTestRestoreAt) return;
                bool restored = ConVar.Find("bot_dont_shoot")!.StringValue == _defenseTestOriginalShoot;
                if (!restored) throw new InvalidOperationException("Defense 射击参数未恢复。");
                var report = new { Result = "PASS_DEFENSE_SERVER_AUTOMATION", NativeShots = _probeShots, NativeDamage = _probeDamage, SparseRoles = new[] { 1, 3, 5 },
                    Handoffs = _defenseTestHandoffs, DeathIsolation = _defenseTestIsolated, _defenseStats.Rounds, _defenseStats.Success, _defenseStats.Failure, _defenseStats.Timeouts,
                    CvarsRestored = restored, NativeBotKilledCT = _defenseTestNativeKill, HumanClientTested = false, TimeUtc = DateTimeOffset.UtcNow };
                File.WriteAllText(Path.Combine(_dataRoot, "defense-selftest-report.json"), JsonSerializer.Serialize(report));
                DefenseNotify(JsonSerializer.Serialize(report)); EndDefenseSelfTest();
            }
        }
        catch (Exception e)
        {
            File.WriteAllText(Path.Combine(_dataRoot, "defense-selftest-report.json"), JsonSerializer.Serialize(new { Result = "FAIL_DEFENSE_AUTOMATION", Phase = _defenseTestPhase, Error = e.Message, TimeUtc = DateTimeOffset.UtcNow }));
            DefenseNotify("自测失败：" + e.Message); EndDefenseSelfTest();
        }
    }
    private void EndDefenseSelfTest()
    {
        if (_defenseTestPhase == 0) return;
        StopDefenseProbe(); CloseDefense("自测结束"); _defenseTestPhase = 0;
        if (_defenseTestTarget is { IsValid: true, IsBot: true } p)
        { _api?.Unlock(p.Slot, LockKind.All); if (p.UserId is int id) Server.ExecuteCommand("kickid " + id); }
        Server.ExecuteCommand("bot_quota 0");
        if (_defenseTestTacticId is Guid tacticId)
        {
            if (_store.Load(Server.MapName, tacticId.ToString("N")).Creator == "server-defense-selftest") _store.Delete(Server.MapName, tacticId.ToString("N"));
            string path = Path.Combine(_dataRoot, "defense", "0", tacticId.ToString("N") + ".json"); if (File.Exists(path)) File.Delete(path);
        }
        _defenseTactic = null; _defenseSettings = null;
        if (_defenseTestOldStats != null) _defenseStats = _defenseTestOldStats;
        _defenseTestOldStats = null; _defenseTestTarget = null;
    }
}
