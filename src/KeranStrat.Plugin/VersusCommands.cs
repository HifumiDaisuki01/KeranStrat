using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using KeranStrat.Core;

namespace KeranStrat.Plugin;

public sealed partial class KeranStratPlugin
{
    private VersusScene? _versusScene;
    private VersusStore _versusStore = null!;
    private PracticeIdentity? _versusOwner;
    private readonly Dictionary<int, Track> _versusAnchors = [];
    private string _versusState = "idle", _versusError = "none";
    private bool _versusIO, _modeVersus;
    private int _versusGeneration, _versusAt, _versusRecordStart;
    private string _versusRecordingKey = "";
    private Track? _versusRecording;
    private readonly PracticeTeamRespawn _versusRespawn = new();
    private bool VersusLease => _versusOwner.HasValue || _versusIO || _versusTestPhase != 0;
    private bool VersusBusy => _versusIO || _versusState != "idle";
    private bool VersusAuthorized(CCSPlayerController p) => p.IsValid && !p.IsBot && !p.IsHLTV && p.UserId != null &&
        AccessPolicy.Allows(Config.VersusEnabled, _versusAllowed, p.AuthorizedSteamID?.SteamId64.ToString());
    private void InitializeVersusStore() => _versusStore = new VersusStore(Path.Combine(_dataRoot, "versus"), Config.MaxTacticsPerMap,
        Config.VersusMaxFileMegabytes * 1024L * 1024, MaxTicks, Config.VersusMaxStorageMegabytes * 1024L * 1024);
    private CCSPlayerController? VersusPlayer()
    {
        if (_versusOwner is not { } identity) return null;
        var p = NativeReplayEngine.Player(identity.Slot);
        return p is { IsValid: true, IsBot: false } && p.UserId == identity.UserId && p.EntityHandle.Raw == identity.Handle &&
            p.AuthorizedSteamID?.SteamId64 == identity.SteamId ? p : null;
    }
    private void VersusNotify(string message)
    {
        Server.PrintToConsole("[KeranStrat Versus] " + message);
        VersusPlayer()?.PrintToChat("[KeranStrat Versus] " + message);
    }
    private void VersusRequireFree()
    {
        if (DefenseLease || DefenseBusy || _modeChanging || _state != "idle" || _busy || _probePhase != 0 ||
            _defenseTestPhase != 0 || _coopTestPhase != 0 || _selfTestPhase != 0)
            throw new InvalidOperationException("KS／Defense 或切图占用中，请先停止并释放会话。");
    }
    private static int VersusTeam(string s) => s.ToLowerInvariant() switch { "t" => 2, "ct" => 3, _ => throw new ArgumentException("阵营必须为 t 或 ct。") };
    private static int VersusRole(string s) => int.TryParse(s, out int role) && role is >= 1 and <= 5 ? role : throw new ArgumentException("角色编号必须为 1–5。");
    private void VersusJob<T>(Func<T> work, Action<T> done)
    {
        int generation = _versusGeneration; _versusIO = true;
        _ = Task.Run(() =>
        {
            try { var result = work(); _jobs.Enqueue(() => { if (generation != _versusGeneration) return; _versusIO = false; try { done(result); } catch (Exception e) { _versusError = e.Message; VersusNotify("操作失败：" + e.Message); } }); }
            catch (Exception e) { _jobs.Enqueue(() => { if (generation != _versusGeneration) return; _versusIO = false; VersusNotify("文件操作失败：" + e.Message); }); }
        });
    }
    private void VersusSaveThen(Action next)
    {
        if (_versusState == "recording") FinishVersusRecording();
        StopVersus("切换环境");
        if (_versusScene == null) { next(); return; }
        var snapshot = _versusScene.Snapshot();
        VersusJob(() => { _versusStore.Save(snapshot); return true; }, _ => next());
    }
    private void VersusCommand(CCSPlayerController? p, CommandInfo cmd, string? forced = null)
    {
        void Reply(string s) => cmd.ReplyToCommand("[KeranStrat Versus] " + s);
        string action = forced ?? (cmd.ArgCount > 1 ? cmd.GetArg(1).ToLowerInvariant() : "help");
        if (action == "help") { ReplyHelp(cmd, "ksv"); return; }
        if (p != null && !VersusAuthorized(p)) { Reply("你没有权限使用 Versus 模式。"); return; }

        try
        {

            if (action == "status")
            {
                Reply($"mode={_trainingMode}, changing={_modeChanging}, state={_versusState}, fileBusy={_versusIO}, scene={_versusScene?.Name ?? "none"}, replay={_versusScene?.Mode}, repeat={_versusScene?.Repeat}, rounds={_versusRounds}, result={_versusLastResult}, error={_versusError}");
                if (_versusScene != null) Reply("角色：" + string.Join(',', _versusScene.Tracks.Keys.Order()));
                if (_versusCoordinator != null) foreach (var e in _versusCoordinator.Entries) Reply($"{e.Key}: {e.Phase}, start={e.Started}, handoff={e.HandedOff}");
                return;
            }
            if (action == "xray") { XRayCommand(p, cmd, true); return; }
            if (p == null)
            {
                if (action == "stop") { StopVersus("服务端停止"); return; }
                if (action == "reload") { ReloadSettings(); Reply("配置已重载。"); return; }
                throw new InvalidOperationException("此操作需要 Versus 白名单真人在游戏内执行。");
            }
            VersusRequireFree();
            if (_versusOwner is { } owner && owner != PracticeIdentityOf(p)) throw new InvalidOperationException("当前 KSV 会话由其他玩家控制。");
            if (_versusIO) throw new InvalidOperationException("文件操作中，请稍后。");
            if (action == "list")
            {
                string map = Server.MapName;
                var identity = PracticeIdentityOf(p);
                VersusJob(() => _versusStore.List(map), list =>
                {
                    if (!p.IsValid || p.UserId != identity.UserId || p.EntityHandle.Raw != identity.Handle) return;
                    p.PrintToChat("[KeranStrat Versus] 本地图 KSV 场景：" + list.Count);
                    foreach (var s in list) p.PrintToChat($"{s.Id:N} {s.Name} ({s.Tracks.Count} 角色)");
                }); return;
            }
            if (action == "reload") { ReloadSettings(); Reply("配置已重载。"); return; }
            if (action == "stop")
            {
                if (_versusState == "recording") FinishVersusRecording(); else StopVersus("已停止");
                Reply("已停止，保留场景与 Practice。"); return;
            }
            if (action is "practice" or "exit" or "map")
            {
                if (action == "practice")
                {
                    _modeDefense = false; _modeVersus = true;
                    if (_trainingMode == "Practice") { Reply("KSV Practice 已就绪。"); return; }
                    EnterTraining(); Reply("正在载入 KSV Practice。"); return;
                }
                if (_trainingMode != "Practice") throw new InvalidOperationException("当前未处于 Practice。");
                string target = action == "exit" ? ValidateMap(Config.DefaultMap) : ValidateMap(cmd.GetArg(2));
                VersusSaveThen(() => { _versusOwner = null; _versusScene = null; _versusAnchors.Clear(); SwitchMode(target, action != "exit"); });
                Reply("保存场景后切换地图／模式。"); return;
            }
            if (_versusState != "idle")
            {
                if (action == "repeat") { SetVersusRepeat(cmd.GetArg(2)); Reply("循环设置已更新。"); return; }
                throw new InvalidOperationException("请先 !ksv stop。");
            }
            if (_trainingMode != "Practice" || !_practice.Active) throw new InvalidOperationException("先 !ksv practice。");
            if (!Bind()) throw new InvalidOperationException("原生 BotController 未就绪。");
            _modeDefense = false; _modeVersus = true;
            if (action is "new" or "load")
            {
                var identity = PracticeIdentityOf(p); string query = Args(cmd, 2), map = Server.MapName, version = _gameVersion;
                double interval = Server.TickInterval;
                if (action == "new")
                {
                    VersusScene.ValidateName(query);
                    if (_versusScene?.Map == map && _versusScene.Name.Equals(query, StringComparison.OrdinalIgnoreCase)) throw new IOException("已存在该战术，请继续编辑或换名。");
                    string defaultMode = Config.VersusDefaultMode; int seconds = Config.VersusRoundSeconds;
                    VersusJob(() => { _versusStore.EnsureNameAvailable(map, query); return new VersusScene { Name = query, Map = map, Creator = identity.SteamId.ToString(), GameVersion = version, TickInterval = interval, Mode = defaultMode, RoundSeconds = seconds }; }, scene =>
                    { _versusOwner = identity; _versusScene = scene; _versusAnchors.Clear(); VersusNotify("已创建独立 KSV 场景 " + scene.Name); });
                }
                else VersusJob(() => _versusStore.Load(map, query), scene =>
                {
                    if (scene.GameVersion != _gameVersion || Math.Abs(scene.TickInterval - Server.TickInterval) > .000001) throw new InvalidDataException("游戏版本或 Tick 间隔不兼容。");
                    _versusOwner = identity; _versusScene = scene; _versusAnchors.Clear(); VersusNotify("已加载 " + scene.Name);
                });
                return;
            }
            if (_versusScene == null || !VersusLease) throw new InvalidOperationException("先 !ksv new 或 load。");
            switch (action)
            {
                case "save":
                    var snapshot = _versusScene.Snapshot(); VersusJob(() => { _versusStore.Save(snapshot); return true; }, _ => VersusNotify("KSV 场景已原子保存。")); break;
                case "anchor":
                    int team = VersusTeam(cmd.GetArg(2));
                    if (p.TeamNum != team || !p.PawnIsAlive) throw new InvalidOperationException("请在对应阵营存活时设置起点。");
                    _versusAnchors[team] = Inventory.Capture(p, 1); Reply("已保存该阵营新角色起点。"); break;
                case "record": StartVersusRecording(p, VersusTeam(cmd.GetArg(2)), VersusRole(cmd.GetArg(3))); break;
                case "remove":
                    string removed = VersusScene.Key(VersusTeam(cmd.GetArg(2)), VersusRole(cmd.GetArg(3)));
                    if (!_versusScene.Tracks.Remove(removed)) throw new InvalidOperationException("该角色未录制。");
                    _versusScene.Handoff.Remove(removed); Reply("已移除，!ksv save 保存。"); break;
                case "offset":
                case "handoff":
                    string key = VersusScene.Key(VersusTeam(cmd.GetArg(2)), VersusRole(cmd.GetArg(3)));
                    if (!_versusScene.Tracks.TryGetValue(key, out var track)) throw new InvalidOperationException("该角色未录制。");
                    if (action == "handoff" && cmd.GetArg(4).Equals("auto", StringComparison.OrdinalIgnoreCase)) _versusScene.Handoff[key] = null;
                    else
                    {
                        int limit = action == "offset" ? 120000 : Config.MaxRecordingSeconds * 1000;
                        if (!int.TryParse(cmd.GetArg(4), out int ms) || ms < 0 || ms > limit) throw new ArgumentException($"时间必须为 0–{limit} 毫秒。");
                        if (action == "offset") track.OffsetMs = ms;
                        else { DefenseBotController.HandoffTicks(ms, track, _versusScene.TickInterval); _versusScene.Handoff[key] = ms; }
                    }
                    Reply("已更新，!ksv save 保存。"); break;
                case "mode":
                    string mode = cmd.GetArg(2).ToLowerInvariant();
                    if (mode is not ("strict" or "hybrid")) throw new ArgumentException("模式必须为 strict 或 hybrid。");
                    _versusScene.Mode = mode; Reply("已切换为 " + mode); break;
                case "repeat": SetVersusRepeat(cmd.GetArg(2)); Reply("循环已更新。"); break;
                case "play": StartVersus(p); break;
                default: throw new ArgumentException("未知指令，输入 !ksv help 查看使用方法：https://cs.jinhsi.cn/help");
            }
        }
        catch (Exception e) { _versusError = e.Message; Reply(e.Message); Log("KSV 指令失败：" + e.Message); }
    }
    private void SetVersusRepeat(string value)
    {
        if (_versusScene == null || value is not ("on" or "off")) throw new ArgumentException("用法：!ksv repeat on/off。");
        _versusScene.Repeat = value == "on";
    }
    private void StartVersusRecording(CCSPlayerController p, int team, int role)
    {
        if (p.TeamNum != team || !p.PawnIsAlive) throw new InvalidOperationException("请在对应阵营存活时录制。");
        string requestedKey = VersusScene.Key(team, role);
        if (!_versusScene!.Tracks.ContainsKey(requestedKey) && _versusScene.Tracks.Keys.Count(k => VersusScene.Parse(k).Team == team) >= Config.VersusMaxPlayersPerTeam)
            throw new InvalidOperationException("该阵营人数已达到配置上限。");
        _versusRecordingKey = VersusScene.Key(team, role);
        var initial = _versusScene!.Tracks.GetValueOrDefault(_versusRecordingKey) ?? _versusAnchors.GetValueOrDefault(team);
        if (initial != null) Inventory.RestorePose(p, initial.Initial);
        _versusRecording = new Track { Role = role, OffsetMs = initial?.OffsetMs ?? 0 };
        _versusAt = Server.TickCount + DefenseTicks(Config.CountdownSeconds * 1000); _versusState = "countdown";
        VersusNotify("准备录制 " + _versusRecordingKey + "；!ksv stop 或绑定 css_ksv_stop 结束。");
    }
    private void FinishVersusRecording()
    {
        var p = VersusPlayer() ?? throw new InvalidOperationException("录制玩家已离线。");
        if (!_api!.StopRecord(p.Slot)) throw new InvalidOperationException("原生录制停止失败。");
        var motion = _api.GetRecordedMotionExtended(p.Slot);
        _versusState = "idle";
        if (motion.ticks.Length == 0) { _versusRecording = null; VersusNotify("录制为空，原角色未改。"); return; }
        var track = _versusRecording!; track.Ticks = motion.ticks; track.Subticks = motion.subs; track.Commands = motion.commands;
        var snapshot = _versusScene!.Snapshot(); snapshot.Tracks[_versusRecordingKey] = track; snapshot.Handoff.Remove(_versusRecordingKey); snapshot.Validate(MaxTicks);
        _versusScene = snapshot; _versusRecording = null;
        p.PrintToCenterHtml(" ", 1); VersusNotify($"{_versusRecordingKey} 已录制 {track.Ticks.Length} 帧，交接重置为 auto；!ksv save 保存。");
    }
}
