using BotControllerApi;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using KeranStrat.Core;

namespace KeranStrat.Plugin;

public sealed partial class KeranStratPlugin
{
    private int _selfTestPhase;
    private int _testDeadline;
    private int _testRecordEnd;
    private string _testBotName = "";
    private int _testBotSlot = -1;
    private long _testMovement;
    private Track? _testTrack;
    private Guid? _testTacticId;
    private double _testDisplacement;
    private readonly HashSet<int> _testBaseline = [];
    private readonly int[][] _testGroups = [[1], [1, 3], [1, 3, 5], [1, 2, 3, 5], [1, 2, 3, 4, 5], [1, 3, 5]];
    private int _testGroupIndex;
    private readonly List<object> _testCaseReports = [];
    private bool _testAllPassed;
    private bool _testLoadoutPassed;

    // Console-only, refuses a live human session. This is not a substitute for human grenade tests.
    private void StartSelfTest(CCSPlayerController? player, CommandInfo command)
    {
        if (player != null || OtherHumans(0) || !_practice.Active || _trainingMode != "Practice" || _modeChanging || _busy || _state != "idle")
            throw new InvalidOperationException("自测仅服务端控制台可用，需要无真人在线，先 css_ks practice 并等待训练就绪。");
        if (!Bind()) throw new InvalidOperationException("BotController 不可用。");
        _owner = 0; _ownerSlot = -1; _testCaseReports.Clear(); _testAllPassed = true; _testGroupIndex = 0;
        _selfTestPhase = 1; _state = "selftest";
        _testDeadline = Server.TickCount + ReplayCoordinator.ToTicks(120000, Server.TickInterval);
        _testBotName = "KS_TEST_" + Guid.NewGuid().ToString("N")[..8];
        _testBaseline.Clear();
        foreach (var p in Utilities.GetPlayers()) if (p.IsValid && p.UserId is int id) _testBaseline.Add(id);
        Server.ExecuteCommand("bot_add_t");
        Server.ExecuteCommand("bot_quota 1");
        command.ReplyToCommand("[KeranStrat] 自动自测已启动：原生 BOT 输入录制、文件往返、五 BOT 同步与偏移回放。真人投掷另行验收。");
    }
    private bool SelfTestTick()
    {
        if (_selfTestPhase == 0) return false;
        if (OtherHumans(0)) throw new InvalidOperationException("真人加入，自测已中断。");
        if (Server.TickCount > _testDeadline) throw new TimeoutException("自动自测超时。");
        if (_selfTestPhase == 1)
        {
            var bot = Utilities.GetPlayers().FirstOrDefault(p => p.IsValid && p.IsBot && p.UserId is int id && !_testBaseline.Contains(id));
            if (bot == null) return true;
            bot.PlayerName = _testBotName;
            Utilities.SetStateChanged(bot, "CBasePlayerController", "m_iszPlayerName");
            if (!bot.PawnIsAlive) { bot.Respawn(); return true; }
            _testBotSlot = bot.Slot;
            GivePracticeKit(bot, "awp");
            GivePracticeKit(bot); // Refilling must preserve the selected AWP.
            _testTrack = Inventory.Capture(bot, 1);
            Inventory.Restore(bot, _testTrack);
            _testLoadoutPassed = _testTrack.Equipment.Any(e => e.ClassName == "weapon_awp") &&
                !_testTrack.Equipment.Any(e => e.ClassName == "weapon_ak47") && Inventory.Count(bot, "weapon_flashbang") == 2 &&
                Inventory.Capture(bot, 1).Ammo.SequenceEqual(_testTrack.Ammo);
            if (!_testLoadoutPassed) throw new InvalidOperationException("AWP + 双闪装备保存/恢复测试失败。");
            _botIdentities[bot.Slot] = bot.Handle;
            if (!_api!.SetReplayPawn(bot.Slot, bot.PlayerPawn.Value!.Handle) || !_api.StartRecord(bot.Slot))
                throw new InvalidOperationException("自测 BOT 原生录制启动失败。");
            _testMovement = _api.StartUsercmdMovement(bot.Slot, 200, 0);
            if (_testMovement <= 0) throw new InvalidOperationException("自测模拟输入 Hook 不可用。");
            _testRecordEnd = Server.TickCount + 96; _selfTestPhase = 2;
            return true;
        }
        if (_selfTestPhase == 2)
        {
            if (Server.TickCount < _testRecordEnd) return true;
            _api!.CancelUsercmdMovement(_testBotSlot, _testMovement); _testMovement = 0;
            if (!_api.StopRecord(_testBotSlot)) throw new InvalidOperationException("自测停止录制失败。");
            var motion = _api.GetRecordedMotionExtended(_testBotSlot);
            if (motion.ticks.Length < 16) throw new InvalidOperationException("原生未记录足够 usercmd 帧。");
            _testTrack!.Ticks = motion.ticks; _testTrack.Subticks = motion.subs; _testTrack.Commands = motion.commands;
            var a = motion.ticks[0].Pre; var b = motion.ticks[^1].Post;
            _testDisplacement = Math.Sqrt(Math.Pow(a.OriginX - b.OriginX, 2) + Math.Pow(a.OriginY - b.OriginY, 2) + Math.Pow(a.OriginZ - b.OriginZ, 2));
            if (_testDisplacement < 1) throw new InvalidOperationException("原生有帧但 BOT 未发生位移，不能宣称移动回放通过。");
            _tactic = new Tactic { Name = "selftest-" + Guid.NewGuid().ToString("N")[..8], Map = Server.MapName,
                Creator = "server-selftest", TickInterval = Server.TickInterval, GameVersion = _gameVersion };
            for (int i = 1; i <= 5; i++)
                _tactic.Tracks[i] = new Track { Role = i, Initial = _testTrack.Initial, Equipment = _testTrack.Equipment, Ammo = _testTrack.Ammo,
                    Health = _testTrack.Health, Armor = _testTrack.Armor, OffsetMs = i == 5 ? 250 : 0,
                    Ticks = motion.ticks, Subticks = motion.subs, Commands = motion.commands };
            _testTacticId = _tactic.Id;
            var saved = _tactic; _selfTestPhase = 3;
            Background(() => { _store.Save(saved); return _store.Load(saved.Map, saved.Id.ToString("N")); }, loaded =>
            {
                _tactic = loaded;
                Server.ExecuteCommand("bot_quota 0");
                KickOwnedBot(_testBotName, _testBotSlot); _testBotName = ""; _testBotSlot = -1;
                _selfTestPhase = 4; BeginReplay(_testGroups[_testGroupIndex]);
            });
            return true;
        }
        if (_selfTestPhase == 3) return true;
        if (_state != "idle") return false;
        var entries = _replay!.Entries;
        bool sync = entries.Count == _testGroups[_testGroupIndex].Length &&
            entries.All(e => e.StartedTick == _replay.StartTick + e.OffsetTicks && e.Finished);
        _testAllPassed &= sync;
        _testCaseReports.Add(new { Roles = _testGroups[_testGroupIndex], Passed = sync,
            Entries = entries.Select(e => new { e.Role, e.StartedTick, ExpectedTick = _replay.StartTick + e.OffsetTicks, e.MaxCursorDrift, e.MaxPositionError, e.PositionSamples }).ToArray() });
        _testGroupIndex++;
        if (_testGroupIndex < _testGroups.Length) { BeginReplay(_testGroups[_testGroupIndex]); return true; }
        var report = new { Result = _testAllPassed ? "PASS_SERVER_AUTOMATION" : "FAIL_SYNC", Abi = _api!.AbiVersion,
            Frames = _testTrack!.Ticks.Length, RecordedDisplacement = _testDisplacement, AwpAndTwoFlashbangs = _testLoadoutPassed,
            Cases = _testCaseReports,
            HumanClientTested = false, GrenadesTested = false, TimeUtc = DateTimeOffset.UtcNow };
        File.WriteAllText(Path.Combine(_dataRoot, "selftest-report.json"), System.Text.Json.JsonSerializer.Serialize(report));
        Notify("自动自测 " + report.Result + "；仅证明服务端 BOT 自动化路径，真人射击/投掷仍待实测。");
        var map = _tactic!.Map; var id = _testTacticId?.ToString("N");
        Abort("自测完成", false); _tactic = null;
        if (id != null) Background(() => { _store.Delete(map, id); return true; }, _ => { Log("自测临时战术已清理。"); ExitTraining(); });
        else ExitTraining();
        return true;
    }
    private void CancelSelfTest()
    {
        if (_selfTestPhase == 0) return;
        if (_practice.Active) Server.ExecuteCommand("bot_quota 0");
        try
        {
            if (_testBotSlot >= 0) _api?.StopRecord(_testBotSlot);
            if (_testMovement > 0 && _testBotSlot >= 0) _api?.CancelUsercmdMovement(_testBotSlot, _testMovement);
        }
        catch (Exception e) { Log("清理自测录制: " + e.Message); }
        if (_testBotName.Length > 0) KickOwnedBot(_testBotName, _testBotSlot);
        _selfTestPhase = 0; _testBotSlot = -1; _testMovement = 0; _testBotName = "";
    }
}
