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
    private int _versusTestPhase, _versusTestAt, _versusTestDeadline, _versusTestCase, _versusTestSeen, _versusTestSettle;
    private int _versusTestAiDamage, _versusTestStrictDamage, _versusTestKills, _versusTestGlows;
    private readonly List<VersusBot> _versusFixtures = [];
    private readonly HashSet<int> _versusTestSlots = [];
    private readonly Dictionary<int, long> _versusFixtureMovement = [];
    private Track? _versusFixtureT, _versusFixtureCT, _versusFixtureStrict;
    private Dictionary<string, string> _versusTestCvars = [];
    private readonly List<object> _versusTestCases = [];
    private string _versusTestDirectory = "";
    private bool _versusTestGlowChecked;
    private int _versusTestGlowReadyAt;
    private void RegisterVersusTestEvents()
    {
        RegisterEventHandler<EventPlayerHurt>((e, _) =>
        {
            if (_versusTestPhase == 0 || _versusState != "playing" || !ProbeFirearm(e.Weapon) || e.Attacker == null || e.Userid == null || e.Attacker.TeamNum == e.Userid.TeamNum) return HookResult.Continue;
            var attacker = _versusBots.Values.FirstOrDefault(b => b.Serial == e.Attacker.EntityHandle.Raw && b.UserId == e.Attacker.UserId);
            if (attacker == null || !_versusBots.Values.Any(b => b.Serial == e.Userid.EntityHandle.Raw && b.UserId == e.Userid.UserId)) return HookResult.Continue;
            if (_versusScene?.Mode == "strict") _versusTestStrictDamage += e.DmgHealth;
            else if (_versusCoordinator?.Entries.Any(b => b.Slot == attacker.Slot && b.Phase == VersusPhase.CombatAI) == true) _versusTestAiDamage += e.DmgHealth;
            return HookResult.Continue;
        });
        RegisterEventHandler<EventPlayerDeath>((e, _) =>
        {
            if (_versusTestPhase != 0 && e.Attacker != null && e.Userid != null && e.Attacker.TeamNum != e.Userid.TeamNum &&
                _versusBots.Values.Any(b => b.Serial == e.Attacker.EntityHandle.Raw && b.UserId == e.Attacker.UserId) &&
                _versusBots.Values.Any(b => b.Serial == e.Userid.EntityHandle.Raw && b.UserId == e.Userid.UserId)) _versusTestKills++;
            return HookResult.Continue;
        });
    }
    private void StartVersusTest(CCSPlayerController? p, CommandInfo command)
    {
        if (p != null || OtherHumans(0) || Utilities.GetPlayers().Any(p => p.IsValid && p.IsBot) || VersusLease ||
            _trainingMode != "Practice" || Server.MapName != "de_dust2" || !Config.VersusEnabled || !Bind())
            throw new InvalidOperationException("KSV 自测仅限空服、无 BOT、Dust2 Practice 的服务端控制台。");
        VersusRequireFree();
        _versusTestPhase = 1; _versusTestCase = 0; _versusTestSeen = _versusRounds; _versusTestSettle = 0;
        _versusTestDeadline = Server.TickCount + DefenseTicks(240000);
        _versusTestAiDamage = 0; _versusTestStrictDamage = 0; _versusTestKills = 0; _versusTestGlows = 0;
        _versusTestCases.Clear(); _versusFixtures.Clear(); _versusTestSlots.Clear(); _versusFixtureMovement.Clear(); _versusTestGlowChecked = false;
        _versusTestCvars = VersusScopedCvars.ToDictionary(k => k, k => ConVar.Find(k)!.StringValue);
        _versusTestDirectory = Path.Combine(_dataRoot, "versus-diagnostics", Guid.NewGuid().ToString("N"));
        foreach (string cmd in new[] { "bot_dont_shoot 0", "bot_stop 0", "bot_zombie 0", "bot_difficulty 3", "mp_respawn_on_death_ct 0", "mp_respawn_on_death_t 0", "mp_respawn_immunitytime 0", "bot_add_t", "bot_add_ct", "bot_quota 2" }) Server.ExecuteCommand(cmd);
        command.ReplyToCommand("[KeranStrat Versus] 双阵营原生录制、真实交火与循环诊断开始。");
    }
    private void VersusTestRecord(bool shooting)
    {
        foreach (var b in _versusFixtures)
        {
            var p = VersusResolve(b)!; if (!p.PawnIsAlive) p.Respawn();
            _api!.Lock(p.Slot, LockKind.All); GivePracticeKit(p, "ak47");
            var kit = Inventory.Capture(p, 1); kit.Equipment = kit.Equipment.Where(e => e.ClassName == "weapon_ak47").ToArray(); kit.Ammo = [];
            kit.Initial = p.TeamNum == 2 ? new(-1181, -754, 121, 0, 0, 0) : new(-1053, -754, 121, 0, 180, 0);
            Inventory.Restore(p, kit); DefenseBotFunds(p); p.PlayerPawn.Value!.GunGameImmunity = false;
            // Fixture-only keepalive: recording attack inputs must not kill the recording partner.
            p.PlayerPawn.Value.Health = 10000;
            var captured = Inventory.Capture(p, 1); captured.Health = 100;
            if (!shooting) { if (p.TeamNum == 2) _versusFixtureT = captured; else _versusFixtureCT = captured; }
            else if (p.TeamNum == 2) _versusFixtureStrict = captured;
            _api.SetReplayPawn(p.Slot, p.PlayerPawn.Value.Handle);
            // Record actual bot-generated attack inputs. Native InjectUsercmd is applied
            // after command capture, so it is unsuitable as a strict replay fixture.
            if (shooting && p.TeamNum == 2)
            { _api.Unlock(p.Slot, LockKind.All); _api.Unlock(p.Slot, LockKind.Aim); _api.Unlock(p.Slot, LockKind.Weapon); }
            if (!_api.StartRecord(p.Slot)) throw new InvalidOperationException("双阵营原生录制启动失败。");
            if (!shooting || p.TeamNum != 2) _versusFixtureMovement[p.Slot] = _api.StartUsercmdMovement(p.Slot, 0, 0);
        }
        _versusTestAt = Server.TickCount + (shooting ? DefenseTicks(3000) : 64);
    }
    private void VersusTestFinishRecord(bool shooting)
    {
        foreach (var b in _versusFixtures)
        {
            var p = VersusResolve(b)!;
            if (_versusFixtureMovement.Remove(p.Slot, out long token)) _api!.CancelUsercmdMovement(p.Slot, token);
            _api!.StopRecord(p.Slot); var motion = _api.GetRecordedMotionExtended(p.Slot);
            var track = shooting ? (p.TeamNum == 2 ? _versusFixtureStrict : null) : p.TeamNum == 2 ? _versusFixtureT : _versusFixtureCT;
            if (track == null) continue;
            track.Ticks = motion.ticks; track.Subticks = motion.subs; track.Commands = motion.commands;
            if (track.Ticks.Length < 16) throw new InvalidOperationException("原生录制为空。");
            if (shooting && p.TeamNum == 2)
            {
                int attacks = track.Commands.Count(c => (c.Buttons & 1UL) != 0);
                Log($"KSV strict fixture frames={track.Ticks.Length}, attackCommands={attacks}");
                if (attacks == 0) throw new InvalidOperationException("严格回放诊断没有录到真实射击输入。");
            }
        }
    }
    private void ConfigureVersusTestCase()
    {
        int countT = _versusTestCase switch { 1 => 3, 2 or 3 => 5, _ => 1 };
        int countCT = _versusTestCase switch { 2 => 2, 3 => 5, _ => 1 };
        var scene = new VersusScene { Name = "KSV-native-diagnostic-" + _versusTestCase, Map = Server.MapName, Creator = "server-versus-selftest",
            GameVersion = _gameVersion, TickInterval = Server.TickInterval, RoundSeconds = 10, Mode = _versusTestCase == 4 ? "strict" : "hybrid", Repeat = _versusTestCase == 5 };
        foreach (int team in new[] { 2, 3 }) for (int i = 1; i <= (team == 2 ? countT : countCT); i++)
        {
            var source = team == 2 ? (_versusTestCase == 4 ? _versusFixtureStrict! : _versusFixtureT!) : _versusFixtureCT!;
            var copy = new Tactic { Tracks = new() { [source.Role] = source } }.Snapshot().Tracks[source.Role];
            copy.Role = i; copy.OffsetMs = (i - 1) * 125;
            scene.Tracks[VersusScene.Key(team, i)] = copy;
        }
        var store = new VersusStore(_versusTestDirectory); store.Save(scene); _versusScene = store.Load(scene.Map, scene.Id.ToString());
        StartVersus(null); _versusTestSettle = 0; _versusTestGlowChecked = false; _versusTestGlowReadyAt = 0;
        Log($"KSV diagnostic case={_versusTestCase} {countT}T vs {countCT}CT {scene.Mode}");
    }
    private void VersusTestTick()
    {
        if (_versusTestPhase == 0) return;
        try
        {
            if (OtherHumans(0)) throw new InvalidOperationException("真人加入，停止空服诊断。");
            if (Server.TickCount > _versusTestDeadline) throw new TimeoutException("KSV 服务端诊断超时。");
            if (_versusTestPhase == 1)
            {
                var players = Utilities.GetPlayers().Where(p => p.IsValid && p.IsBot && p.UserId != null && p.TeamNum is 2 or 3).ToArray();
                if (players.Length != 2 || players.Any(p => !p.PawnIsAlive)) return;
                foreach (var p in players) _versusFixtures.Add(new("fixture", p.Slot, p.UserId!.Value, p.EntityHandle.Raw));
                VersusTestRecord(false); _versusTestPhase = 2; return;
            }
            if (_versusTestPhase == 2 && Server.TickCount >= _versusTestAt)
            { VersusTestFinishRecord(false); VersusTestRecord(true); _versusTestPhase = 3; return; }
            if (_versusTestPhase == 3 && Server.TickCount >= _versusTestAt)
            {
                VersusTestFinishRecord(true);
                foreach (var b in _versusFixtures) if (VersusResolve(b) is { } p) { _api!.Unlock(p.Slot, LockKind.All); Server.ExecuteCommand("kickid " + p.UserId!.Value); }
                Server.ExecuteCommand("bot_quota 0"); _versusTestAt = Server.TickCount + 2; _versusTestPhase = 4; return;
            }
            if (_versusTestPhase == 4)
            {
                if (_versusFixtures.Any(b => VersusResolve(b) != null) || Server.TickCount < _versusTestAt) return;
                ConfigureVersusTestCase(); _versusTestPhase = 5; return;
            }
            if (_versusTestPhase != 5) return;
            foreach (var b in _versusBots.Values) _versusTestSlots.Add(b.Slot);
            if (_versusState == "preparing" && _versusBots.Count == _versusScene!.Tracks.Count && !_versusTestGlowChecked)
            {
                _xrayVersus = true;
                if (_versusTestGlowReadyAt == 0)
                {
                    foreach (var b in _versusBots.Values)
                        _xray[b.Key] = XRayCreate(b.Key, VersusResolve(b)!.PlayerPawn.Value!);
                    _versusTestGlowReadyAt = Server.TickCount + 2;
                    return;
                }
                if (Server.TickCount < _versusTestGlowReadyAt) return;
                foreach (var b in _versusBots.Values)
                {
                    var proxy = _xray[b.Key];
                    if (!proxy.Initialized || proxy.Retiring || proxy.Glow.Glow.GlowType != 3 || proxy.Glow.Glow.GlowColorOverride.ToArgb() != System.Drawing.ColorTranslator.FromHtml(Config.VersusXRayColors[b.Key]).ToArgb())
                        throw new InvalidOperationException("原生轮廓实体属性或角色颜色不匹配。");
                    _versusTestGlows++;
                }
                XRayClear(); // Entity removal can complete on the following engine frame.
                _versusTestGlowChecked = true;
            }
            if (_versusRounds > _versusTestSeen)
            {
                _versusTestCases.Add(new { Case = _versusTestCase, Mode = _versusScene!.Mode, Roles = _versusScene.Tracks.Keys.ToArray(), Result = _versusLastResult,
                    Timeline = _versusCoordinator!.Entries.Select(e => new { e.Key, e.Started, e.HandedOff, Phase = e.Phase.ToString() }).ToArray(), GlowEntitiesCreatedAndRemoved = _versusTestGlowChecked });
                _versusTestSeen = _versusRounds;
                if (_versusTestCase == 5 && _versusTestCases.Count == 6) return; // One additional automatic repeat.
                _versusScene.Repeat = false;
            }
            if (_versusState != "idle") return;
            if (_versusError != "none") throw new InvalidOperationException(_versusError);
            if (_versusTestSettle == 0) { _versusTestSettle = Server.TickCount + 2; return; }
            if (Server.TickCount < _versusTestSettle) return;
            _versusTestCase++;
            if (_versusTestCase <= 5) { ConfigureVersusTestCase(); return; }
            if (_versusTestAiDamage < 1 || _versusTestKills < 1 || _versusTestStrictDamage < 1 || _xray.Count != 0) throw new InvalidOperationException("未确认原生 AI、严格回放真实枪伤、击杀或轮廓清理。");
            if (_versusTestSlots.Any(slot => _api!.IsLocked(slot, LockKind.All) || _api.IsLocked(slot, LockKind.Aim) || _api.GetWeaponLock(slot) != LockTarget.None))
                throw new InvalidOperationException("KSV BOT 清理后仍残留原生锁。");
            var result = new { Result = "PASS_VERSUS_SERVER_AUTOMATION", Cases = _versusTestCases, NativeAIGunDamage = _versusTestAiDamage,
                StrictReplayGunDamage = _versusTestStrictDamage, ActualKillEvents = _versusTestKills, GlowPairsCreatedAndRemoved = _versusTestGlows,
                NativeLocksReleased = true, HumanClientRendered = false, ClientVisibilityIsolationTested = false, TimeUtc = DateTimeOffset.UtcNow };
            File.WriteAllText(Path.Combine(_dataRoot, "versus-selftest-report.json"), JsonSerializer.Serialize(result));
            Log(JsonSerializer.Serialize(result)); EndVersusTest();
        }
        catch (Exception e)
        {
            File.WriteAllText(Path.Combine(_dataRoot, "versus-selftest-report.json"), JsonSerializer.Serialize(new { Result = "FAIL_VERSUS_SERVER_AUTOMATION", Case = _versusTestCase, Phase = _versusTestPhase, Error = e.ToString(), NativeAIGunDamage = _versusTestAiDamage, StrictReplayGunDamage = _versusTestStrictDamage, Kills = _versusTestKills, TimeUtc = DateTimeOffset.UtcNow }));
            Log("KSV diagnostic failed: " + e); EndVersusTest();
        }
    }
    private void EndVersusTest()
    {
        if (_versusTestPhase == 0) return;
        StopVersus("诊断结束", close: true);
        foreach (var b in _versusFixtures) if (VersusResolve(b) is { } p)
        {
            if (_versusFixtureMovement.Remove(p.Slot, out long token)) _api?.CancelUsercmdMovement(p.Slot, token);
            _api?.StopRecord(p.Slot); _api?.Unlock(p.Slot, LockKind.All); Server.ExecuteCommand("kickid " + p.UserId!.Value);
        }
        _versusFixtures.Clear();
        foreach (var (key, value) in _versusTestCvars) Server.ExecuteCommand(key + " " + value);
        if (_versusTestDirectory.Length > 0 && Directory.Exists(_versusTestDirectory))
        {
            string parent = Path.GetFullPath(Path.Combine(_dataRoot, "versus-diagnostics"));
            if (Path.GetDirectoryName(Path.GetFullPath(_versusTestDirectory)) != parent) throw new InvalidOperationException("诊断目录越界。");
            Directory.Delete(_versusTestDirectory, true);
        }
        _versusTestPhase = 0;
    }
}
