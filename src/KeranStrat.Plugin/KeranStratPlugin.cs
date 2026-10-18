using System.Collections.Concurrent;
using System.Text.Json;
using BotControllerApi;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Commands;
using KeranStrat.Core;
using Microsoft.Extensions.Logging;

namespace KeranStrat.Plugin;

public sealed class KeranStratConfig : BasePluginConfig
{
    public bool Enabled { get; set; } = true;
    public string[] AllowedSteamIDs { get; set; } = [];
    public bool DefenseEnabled { get; set; } = true;
    public string[] DefenseAllowedSteamIDs { get; set; } = [];
    public int DefenseRoundSeconds { get; set; } = 120;
    public bool VersusEnabled { get; set; } = true;
    public string[] VersusAllowedSteamIDs { get; set; } = [];
    public int VersusMaxPlayersPerTeam { get; set; } = 5;
    public string VersusDefaultMode { get; set; } = "hybrid";
    public int VersusRoundSeconds { get; set; } = 120;
    public int VersusMaxFileMegabytes { get; set; } = 64;
    public int VersusMaxStorageMegabytes { get; set; } = 256;
    public bool KsXRayDefault { get; set; }
    public Dictionary<string, string> KsXRayColors { get; set; } = XRayPolicy.KsDefaults();
    public bool VersusXRayDefault { get; set; } = true;
    public Dictionary<string, string> VersusXRayColors { get; set; } = XRayPolicy.VersusDefaults();
    public string DefaultMap { get; set; } = "de_dust2";
    public string DefaultGameMode { get; set; } = "deathmatch";
    public int MaxRecordedPlayers { get; set; } = 5;
    public string[] PracticeGrenades { get; set; } = ["weapon_smokegrenade", "weapon_flashbang", "weapon_hegrenade", "weapon_molotov", "weapon_decoy"];
    // Retained for old configuration compatibility; no longer used for authorization.
    public string AdminFlag { get; set; } = "@css/root";
    public string DataDirectory { get; set; } = "data";
    public int CountdownSeconds { get; set; } = 3;
    public int MaxRecordingSeconds { get; set; } = 180;
    public int MaxTacticsPerMap { get; set; } = 100;
    public int MaxFileMegabytes { get; set; } = 64;
    public int MaxStorageMegabytes { get; set; } = 256;
    public Dictionary<string, string> PracticeCvars { get; set; } = new(PracticeEnvironment.Defaults);
    public string SourceUrl { get; set; } = "https://cs.jinhsi.cn/keranstrat-source.tar.gz";
}

public sealed partial class KeranStratPlugin : BasePlugin, IPluginConfig<KeranStratConfig>
{
    public override string ModuleName => "KeranStrat";
    public override string ModuleAuthor => "Keran Technology";
    public override string ModuleVersion => "0.5.2";
    public override string ModuleDescription => "五队员原生动作录制、统一 Tick 回放与本地战术存储 (AGPL-3.0)";
    public KeranStratConfig Config { get; set; } = new();
    private IBotControllerApi? _api;
    private NativeReplayEngine? _engine;
    private ReplayCoordinator? _replay;
    private FileTacticStore _store = null!;
    private PracticeEnvironment _practice = null!;
    private Tactic? _tactic;
    private Track? _anchor;
    private Track? _recording;
    private ulong _owner;
    private int _ownerSlot = -1;
    private int _recordStart;
    private int _countdownAt;
    private int _lastCountdown = -1;
    private int _generation;
    private bool _busy;
    private string _state = "idle";
    private readonly ConcurrentQueue<Action> _jobs = new();
    private readonly List<(int Role, string Name, int Slot)> _bots = [];
    private readonly HashSet<int> _spawnBaseline = [];
    private readonly Dictionary<int, nint> _botIdentities = [];
    private int _prepareDeadline;
    private int _readyAt;
    private string _dataRoot = "";
    private string _gameVersion = "unknown";
    private long _ticksSeen;
    private readonly List<string> _lastReport = [];
    private string _lastError = "none";
    private HashSet<string> _allowed = new(StringComparer.Ordinal);
    private HashSet<string> _versusAllowed = new(StringComparer.Ordinal);

    public void OnConfigParsed(KeranStratConfig config)
    {
        if (config.VersusAllowedSteamIDs.Any(s => !AccessPolicy.IsSteamId64(s)) || config.VersusMaxPlayersPerTeam is < 1 or > 5 ||
            config.VersusDefaultMode is not ("strict" or "hybrid") || config.VersusRoundSeconds is < 10 or > 600 ||
            config.VersusMaxFileMegabytes is < 1 or > 64 || config.VersusMaxStorageMegabytes is < 16 or > 1024)
            throw new ArgumentException("KSV 配置超出范围。");
        config.KsXRayColors = XRayPolicy.Merge(false, config.KsXRayColors);
        config.VersusXRayColors = XRayPolicy.Merge(true, config.VersusXRayColors);
        if (config.CountdownSeconds is < 1 or > 10 || config.MaxRecordingSeconds is < 1 or > 180 ||
            config.MaxTacticsPerMap is < 1 or > 1000 || config.MaxFileMegabytes is < 1 or > 64 || config.MaxStorageMegabytes is < 16 or > 1024 ||
            config.MaxRecordedPlayers is < 1 or > 5 || config.DefaultMap != "de_dust2" || config.DefaultGameMode != "deathmatch" ||
            config.AllowedSteamIDs.Any(s => !AccessPolicy.IsSteamId64(s)) || config.DefenseAllowedSteamIDs.Any(s => !AccessPolicy.IsSteamId64(s)) ||
            config.DefenseRoundSeconds is < 10 or > 600 || config.PracticeGrenades.Length > 5 ||
            config.PracticeGrenades.Any(s => !System.Text.RegularExpressions.Regex.IsMatch(s, "^weapon_(smokegrenade|flashbang|hegrenade|molotov|incgrenade|decoy)$")))
            throw new ArgumentException("KeranStrat 配置超出安全范围或 SteamID64/装备格式无效。");
        var merged = new Dictionary<string, string>(PracticeEnvironment.Defaults);
        foreach (var (key, value) in config.PracticeCvars)
        {
            if (!merged.ContainsKey(key)) throw new ArgumentException("未知练习参数: " + key);
            merged[key] = value;
        }
        config.PracticeCvars = merged;
        Config = config;
        _allowed = new HashSet<string>(config.AllowedSteamIDs, StringComparer.Ordinal);
        _defenseAllowed = new HashSet<string>(config.DefenseAllowedSteamIDs, StringComparer.Ordinal);
        _versusAllowed = new HashSet<string>(config.VersusAllowedSteamIDs, StringComparer.Ordinal);
    }
    private int MaxTicks => checked((int)Math.Ceiling(Config.MaxRecordingSeconds / (double)Server.TickInterval));
    private static PluginCapability<IBotControllerApi> Capability => new("botcontroller:api");
    public override void Load(bool hotReload)
    {
        _dataRoot = Path.GetFullPath(Path.Combine(ModuleDirectory, Config.DataDirectory));
        if (!_dataRoot.StartsWith(Path.GetFullPath(ModuleDirectory) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("DataDirectory 必须位于插件专用目录内。");
        Directory.CreateDirectory(_dataRoot);
        _store = new FileTacticStore(_dataRoot, Config.MaxTacticsPerMap, Config.MaxFileMegabytes * 1024L * 1024, MaxTicks, Config.MaxStorageMegabytes * 1024L * 1024);
        InitializeVersusStore();
        _practice = new PracticeEnvironment(Path.Combine(_dataRoot, "practice-recovery.json"), Config.PracticeCvars);
        RecoverDefenseEnvironment();
        RecoverVersus();
        _practice.Recover();
        try
        {
            string inf = Path.Combine(Server.GameDirectory, "csgo", "steam.inf");
            if (!File.Exists(inf)) inf = Path.Combine(Server.GameDirectory, "steam.inf");
            if (File.Exists(inf)) _gameVersion = File.ReadLines(inf).FirstOrDefault(s => s.StartsWith("ServerVersion=")) ?? "unknown";
        }
        catch (IOException) { /* diagnostics only */ }
        AddCommand("css_ks", "KeranStrat 战术录制与回放", Command);
        AddCommand("css_ks_stop", "KeranStrat 快捷停止录制，可绑定 mouse3", StopShortcut);
        RegisterDefenseProbe();
        AddCommand("css_ksd", "KeranStrat Defense 防守训练", DefenseCommand);
        AddCommand("css_ksd_selftest", "Console-only empty-server Defense round automation", StartDefenseSelfTest);
        AddCommand("css_ksd_cooptest", "Console-only empty-server cooperative Defense automation", StartDefenseCoopTest);
        AddCommand("css_ksv", "KeranStrat Versus 独立双阵营战术模拟", (p, c) => VersusCommand(p, c));
        AddCommand("css_ksv_stop", "KSV 快捷结束录制", (p, c) => VersusCommand(p, c, "stop"));
        AddCommand("css_ksv_selftest", "Console-only empty-server dual-team native simulation test", StartVersusTest);
        RegisterVersusTestEvents();
        RegisterListener<Listeners.CheckTransmit>(XRayTransmit);
        WatchConfiguration();
        RegisterListener<Listeners.OnTick>(Tick);
        RegisterListener<Listeners.OnMapStart>(TrainingMapStarted);
        RegisterListener<Listeners.OnMapEnd>(() => { XRayClear(); StopVersus("地图结束", close: true); CloseDefense("地图结束"); Abort("地图结束", false); _tactic = null; _anchor = null; });
        RegisterEventHandler<EventPlayerDisconnect>((e, _) =>
        {
            if (e.Userid != null) DefenseDisconnected(e.Userid);
            if (e.Userid != null && e.Userid.Slot == _ownerSlot)
            { if (_state != "idle" || _busy) Abort("操作玩家断线", false); _owner = 0; _ownerSlot = -1; }
            return HookResult.Continue;
        }, HookMode.Pre);
        RegisterEventHandler<EventPlayerDeath>((e, _) => { DefenseMarkDeath(e.Userid); VersusDeath(e.Userid); return HookResult.Continue; });
        RegisterEventHandler<EventPlayerConnectFull>((e, _) =>
        {
            if (e.Userid is { IsBot: false, IsValid: true } p)
                p.PrintToChat("[KeranStrat] AGPL 源码: " + Config.SourceUrl);
            return HookResult.Continue;
        });
        RegisterEventHandler<EventPlayerTeam>((e, _) => { PracticeTeamChanged(e.Userid, e.Team, e.Oldteam); VersusTeamChanged(e.Userid, e.Team, e.Oldteam); return HookResult.Continue; }, HookMode.Post);
        RegisterEventHandler<EventPlayerSpawn>((e, _) =>
        {
            var p = e.Userid;
            DefenseHandleSpawn(p);
            if (p != null) _practiceTeamRespawn.Cancel(p.Slot);
            if (p != null) _versusRespawn.Cancel(p.Slot);
            if (p is { IsValid: true, IsBot: false } && _trainingMode == "Practice" && !_modeChanging && _state == "idle" && !DefenseBusy)
            {
                var spawnIdentity = p.EntityHandle.Raw;
                var spawnUserId = p.UserId;
                Server.NextFrame(() =>
                {
                    try
                    {
                        if (p.IsValid && p.EntityHandle.Raw == spawnIdentity && p.UserId == spawnUserId && p.PawnIsAlive && _trainingMode == "Practice" && _state == "idle" && !DefenseBusy)
                        {
                            if (KsPracticeRespawnEnabled && KsPracticeHuman(p)) GivePracticeKit(p);
                            else if (_modeVersus && !VersusBusy && !DefenseLease && VersusAuthorized(p)) GivePracticeKit(p);
                            else if (AccessPolicy.Allows(Config.DefenseEnabled, _defenseAllowed, p.AuthorizedSteamID?.SteamId64.ToString())) DefensePracticeFunds(p);
                        }
                    }
                    catch (Exception error) { Log("训练装备恢复失败: " + error.Message); }
                });
            }
            return HookResult.Continue;
        });
        Server.NextFrame(() =>
        {
            try { Bind(); } catch (Exception e) { Log("绑定失败: " + e.Message); }
        });
        Log("已加载；默认不改变服务器模式。!ks 帮助，!ks source 查看源码。");
    }
    public override void OnAllPluginsLoaded(bool hotReload) => Bind();
    private bool Bind()
    {
        if (_api != null) return true;
        try
        {
            var api = Capability.Get();
            if (api == null || api.AbiVersion != 22) { Log("BotController 不可用或 ABI 不是 22。"); return false; }
            _api = api; _engine = new NativeReplayEngine(api); _replay = new ReplayCoordinator(_engine);
            Log("已连接 BotController ABI 22。"); return true;
        }
        catch (Exception e) { Log("BotController 暂不可用: " + e.Message); return false; }
    }
    public override void Unload(bool hotReload)
    {
        _configWatcher?.Dispose();
        bool restoreMap = _trainingMode == "Practice" || _modeChanging;
        Abort("插件卸载", true);
        if (restoreMap) { SetRules(false); Server.ExecuteCommand("map " + Config.DefaultMap); }
    }
    private void Log(string message)
    {
        Server.PrintToConsole("[KeranStrat] " + message);
        Logger.LogInformation("[KeranStrat] {Message}", message);
    }
    private void Notify(string message)
    {
        Log(message);
        var owner = NativeReplayEngine.Player(_ownerSlot);
        if (owner != null && (_owner == 0 || owner.SteamID == _owner)) owner.PrintToChat("[KeranStrat] " + message);
    }
    private bool OtherHumans(ulong owner) => Utilities.GetPlayers().Any(p => p.IsValid && !p.IsBot && !p.IsHLTV && p.SteamID != owner);
    private void RequireOwner(CCSPlayerController? player)
    {
        if ((_state != "idle" || _busy) && _ownerSlot >= 0 && player != null && (_ownerSlot != player.Slot || _owner != player.SteamID))
            throw new InvalidOperationException("已有其他玩家在操作 KeranStrat。");
    }
    private void RequireReady(CCSPlayerController player, bool allowSpectator = false)
    {
        RequireOwner(player);
        if (_busy || _state != "idle") throw new InvalidOperationException("已有操作进行中，请先 !ks stop 或 !ks cancel。");
        if (!_practice.Active || _trainingMode != "Practice" || _modeChanging) throw new InvalidOperationException("请先 !ks practice，等待训练地图加载完成；!ks exit 恢复死斗。");
        if (!Bind()) throw new InvalidOperationException("BotController 原生/API 依赖未就绪。");
        if (_tactic == null || _tactic.Map != Server.MapName) throw new InvalidOperationException("请先创建或加载当前地图战术。");
        if (!(allowSpectator && player.TeamNum == 1))
        {
            if (_tactic.Team != player.TeamNum) throw new InvalidOperationException("玩家队伍与战术 T/CT 不一致；播放可切换为观察者，录制需加入战术对应阵营。");
            if (!player.PawnIsAlive) throw new InvalidOperationException("请先出生。");
        }
        _owner = player.SteamID; _ownerSlot = player.Slot;
    }
    private void Command(CCSPlayerController? player, CommandInfo cmd) => Command(player, cmd, null);
    private void Command(CCSPlayerController? player, CommandInfo cmd, string? forcedAction)
    {
        string action = forcedAction ?? (cmd.ArgCount > 1 ? cmd.GetArg(1).ToLowerInvariant() : "help");
        void Reply(string message) => cmd.ReplyToCommand("[KeranStrat] " + message);
        if (player != null)
        {
            string? id = null;
            try { if (player.IsValid && !player.IsBot) id = player.AuthorizedSteamID?.SteamId64.ToString(); }
            catch (InvalidOperationException) { /* pending authentication is denied */ }
            if (!AccessPolicy.Allows(Config.Enabled, _allowed, id))
            { Reply("你没有权限使用 KeranStrat。"); return; }
        }
        if (action is "help" or "source")
        {
            Reply(action == "source" ? $"AGPL-3.0；Keran Technology；BotController: XBribo。源码: {Config.SourceUrl}" :
                "!ks practice | exit | map <地图> | kit | new <名称> | record <1-5> | stop | trim <毫秒> [队员] | play | replay <1-5> | remove <1-5> | reset | save | load <名称/ID> | list | delete <名称/ID> | copy <名称> | offset <1-5> <毫秒> | anchor | reload | status | cancel | source；快捷停止绑定：bind mouse3 css_ks_stop");
            return;
        }
        try
        {
            if (action == "xray") { XRayCommand(player, cmd, false); return; }
            if (VersusLease && action != "status")
            {
                if (player == null && action == "cancel") StopVersus("服务端紧急取消", close: true);
                else throw new InvalidOperationException("KSV 会话占用中，请操作者先 !ksv exit。");
            }
            if (DefenseLease && action is not ("help" or "source" or "status"))
            {
                if (action == "exit")
                { if (player != null) DefenseRequireOwner(player); CloseDefense("房主原版指令退出训练"); }
                else if (player == null && action == "cancel") CloseDefense("服务端紧急取消");
                else throw new InvalidOperationException("Defense 会话占用中，请房主先 !ksd close 解散会话。");
            }
            if (action == "status")
            {
                Reply($"mode={_trainingMode}, changing={_modeChanging}, state={_state}, fileBusy={_busy}, practice={_practice.Active}, ABI={_api?.AbiVersion.ToString() ?? "unavailable"}, ticks={_ticksSeen}, selftestPhase={_selfTestPhase}, tactic={_tactic?.Name ?? "none"}, roles={string.Join(',', _tactic?.Tracks.Keys.Order().ToArray() ?? [])}");
                foreach (var line in _lastReport) Reply(line);
                Reply("lastError=" + _lastError);
                var rules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules;
                Reply($"warmup={rules?.WarmupPeriod}, freeze={rules?.FreezePeriod}, setupIssued={_trainingSetupIssued}, phase={rules?.GamePhase}, started={rules?.HasMatchStarted}, waitingResume={rules?.MatchWaitingForResume}");
                return;
            }
            if (action == "selftest") { StartSelfTest(player, cmd); return; }
            if (player == null && action == "cancel")
            {
                Abort("服务端取消", false);
                if (_trainingMode == "Practice") ExitTraining(); else _practice.Exit();
                Reply("已停止；如在训练中将恢复死斗。"); return;
            }
            if (action == "reload") { ReloadSettings(); Reply("白名单及配置已重新加载。"); return; }
            if (_modeChanging) throw new InvalidOperationException("地图或模式正在切换，请稍后。");
            if (action is "practice" or "exit" or "map")
            {
                if (player == null && OtherHumans(0)) throw new InvalidOperationException("自动控制台模式测试要求空服；游戏内白名单用户可提示后切换。");
                if (!Config.Enabled) throw new InvalidOperationException("KeranStrat 已禁用。");
                if (player != null) RequireOwner(player);
                _modeDefense = false;
                _modeVersus = false;
                if (action == "exit" || (action == "practice" && cmd.GetArg(2) == "off")) ExitTraining();
                else if (action == "map") ChangeTrainingMap(cmd.GetArg(2));
                else { _modeDefense = false; if (_trainingMode == "Practice") { Reply("KS Practice 已就绪。"); return; } EnterTraining(); }
                Reply("已请求切换，加载完成后会核对地图与游戏规则。"); return;
            }
            if (player == null) throw new InvalidOperationException("此指令需要真人在游戏内操作；服务端可用 status/selftest/cancel。");
            RequireOwner(player);
            if (action is "cancel" or "reset")
            {
                Abort("操作已取消", false);
                if (action == "reset" && _anchor != null) Inventory.Restore(player, _anchor);
                Reply("已停止并清理自己的 BOT；战术仍保留。practice off 可恢复练习前配置。"); return;
            }
            if (_busy) throw new InvalidOperationException("文件操作中，请稍后。");
            if (_state != "idle" && action != "stop") throw new InvalidOperationException("请先停止当前录制或回放。");
            switch (action)
            {
                case "kit":
                    if (_trainingMode != "Practice" || !player.PawnIsAlive) throw new InvalidOperationException("请先进入训练并选择 T/CT 出生。");
                    GivePracticeKit(player, cmd.ArgCount > 2 ? cmd.GetArg(2) : null); Reply("已补齐装备和训练资金；保留已有主武器。可 !ks kit awp，或按 B 自由购买（不限地点/时间）。"); break;
                case "new":
                    string name = Args(cmd, 2); Tactic.ValidateName(name);
                    if (_tactic?.Map == Server.MapName && _tactic.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("已存在该战术，当前正在编辑；请继续编辑或换一个名称。");
                    if (player.TeamNum is not (2 or 3) || !player.PawnIsAlive) throw new InvalidOperationException("请先选择 T/CT 并出生。");
                    _owner = player.SteamID; _ownerSlot = player.Slot;
                    var candidate = new Tactic { Name = name, Map = Server.MapName, Creator = player.SteamID.ToString(), Team = player.TeamNum,
                        TickInterval = Server.TickInterval, GameVersion = _gameVersion };
                    var candidateAnchor = Inventory.Capture(player, 1);
                    Background(() => { _store.EnsureNameAvailable(candidate.Map, name); return candidate; }, created =>
                    {
                        _tactic = created; _anchor = candidateAnchor; _lastRecordedRole = 0; _hudUntil = 0;
                        Notify("已创建 " + name + "；起点及装备已记住。");
                    }); Reply("正在检查战术名称。"); break;
                case "anchor":
                    RequireReady(player); _anchor = Inventory.Capture(player, 1); Reply("已更新后续新队员的初始位置和装备。"); break;
                case "record":
                    RequireReady(player); StartRecording(player, Role(cmd.GetArg(2))); Reply("准备录制，倒计时结束后行动；!ks stop 结束。"); break;
                case "stop":
                    if (_state == "recording") FinishRecording(); else Abort("已停止", false);
                    break;
                case "play": RequireReady(player, allowSpectator: true); BeginReplay(_tactic!.Tracks.Keys.Order().ToArray()); break;
                case "replay": RequireReady(player, allowSpectator: true); BeginReplay([Role(cmd.GetArg(2))]); break;
                case "offset":
                    if (_tactic == null || !_tactic.Tracks.TryGetValue(Role(cmd.GetArg(2)), out var track)) throw new InvalidOperationException("该队员还没有轨迹。");
                    if (!int.TryParse(cmd.GetArg(3), out int offset) || offset is < 0 or > 120000) throw new ArgumentException("偏移必须为 0–120000 毫秒。");
                    track.OffsetMs = offset; Reply("偏移已更新，使用 !ks save 保存。"); break;
                case "remove":
                    if (_tactic == null || !_tactic.Tracks.Remove(Role(cmd.GetArg(2)))) throw new InvalidOperationException("该队员没有录制。");
                    Reply("已移除该队员，其他轨迹保留；!ks save 保存。"); break;
                case "trim":
                    if (_tactic == null) throw new InvalidOperationException("请先录制或加载战术。");
                    int trimRole = cmd.ArgCount > 3 ? Role(cmd.GetArg(3)) : _lastRecordedRole;
                    if (!_tactic.Tracks.TryGetValue(trimRole, out var original))
                        throw new InvalidOperationException("没有刚录好的轨迹。使用 !ks trim <毫秒> <队员编号> 指定队员。");
                    if (!int.TryParse(cmd.GetArg(2), out int trimMs)) throw new ArgumentException("用法：!ks trim 300 [队员编号]。");
                    var trimmed = TrackEditing.TrimEnd(original, trimMs, _tactic.TickInterval);
                    var edit = _tactic.Snapshot(); edit.Tracks[trimRole] = trimmed; edit.Validate(MaxTicks);
                    _tactic.Tracks[trimRole] = trimmed;
                    Reply($"队员 {trimRole} 已裁掉末尾 {(original.Ticks.Length - trimmed.Ticks.Length) * _tactic.TickInterval * 1000:F1} 毫秒，剩余 {Duration(trimmed.Ticks.Length * _tactic.TickInterval)}。!ks save 保存。"); break;
                case "copy":
                    if (_tactic == null) throw new InvalidOperationException("尚无战术。");
                    string copyName = Args(cmd, 2); Tactic.ValidateName(copyName);
                    if (_tactic.Name.Equals(copyName, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("已存在该战术，副本请使用不同名称。");
                    var copy = _tactic.Snapshot(); copy.Id = Guid.NewGuid(); copy.Name = copyName; copy.Creator = player.SteamID.ToString(); copy.Created = DateTimeOffset.UtcNow;
                    _owner = player.SteamID; _ownerSlot = player.Slot;
                    Background(() => { _store.EnsureNameAvailable(copy.Map, copyName); return copy; }, created =>
                    { _tactic = created; Notify("已复制为新 ID；请 !ks save。"); }); Reply("正在检查副本名称。"); break;
                case "save":
                    if (_tactic == null) throw new InvalidOperationException("没有可保存的战术。");
                    var saved = _tactic.Snapshot(); Background(() => { _store.Save(saved); return saved; }, _ => Notify("战术已原子保存。")); Reply("正在保存。"); break;
                case "load":
                    string map = Server.MapName, query = Args(cmd, 2);
                    Background(() => _store.Load(map, query), t =>
                    {
                        if (Math.Abs(t.TickInterval - Server.TickInterval) > 0.000001 || t.GameVersion != _gameVersion)
                        { Notify("录制的 Tick 间隔或游戏版本不同，已拒绝加载；请用原版本或重新录制。"); return; }
                        _tactic = t; _owner = player.SteamID; _ownerSlot = player.Slot; _anchor = t.Tracks.Values.FirstOrDefault();
                        _lastRecordedRole = 0; _hudUntil = 0;
                        Notify("已加载 " + t.Name + "，队员数 " + t.Tracks.Count);
                    }); Reply("正在加载。"); break;
                case "list":
                    string listMap = Server.MapName;
                    Background(() => _store.List(listMap), list =>
                    {
                        Notify("当前地图战术数 " + list.Count);
                        foreach (var t in list) Notify($"{t.Id:N} {t.Name} ({t.Tracks.Count} 队员)");
                    }); break;
                case "delete":
                    string deleteMap = Server.MapName, deleteName = Args(cmd, 2);
                    Background(() => { _store.Delete(deleteMap, deleteName); return true; }, _ => Notify("已删除指定战术文件。")); break;
                default: throw new ArgumentException("未知指令，!ks 查看帮助。");
            }
        }
        catch (Exception e) { _lastError = e.Message; Reply(e.Message); Log($"指令 {action} 失败: {e.GetType().Name}: {e.Message}"); }
    }
    private int Role(string text) => int.TryParse(text, out int role) && role >= 1 && role <= Config.MaxRecordedPlayers ? role : throw new ArgumentException("队员编号超出配置范围。");
    private static string Args(CommandInfo cmd, int start) => string.Join(' ', Enumerable.Range(start, Math.Max(0, cmd.ArgCount - start)).Select(cmd.GetArg)).Trim();
    private void Background<T>(Func<T> work, Action<T> complete)
    {
        int generation = _generation; _busy = true;
        _ = Task.Run(() =>
        {
            try { var result = work(); _jobs.Enqueue(() => { if (_generation != generation) return; _busy = false; complete(result); }); }
            catch (Exception e) { _jobs.Enqueue(() => { if (_generation != generation) return; _busy = false; Notify("文件操作失败: " + e.Message); }); }
        });
    }
    private void StartRecording(CCSPlayerController player, int role)
    {
        var initial = _tactic!.Tracks.GetValueOrDefault(role) ?? _anchor ?? Inventory.Capture(player, role);
        // Keep the loadout the player has just bought; only reuse the role's starting pose.
        Inventory.RestorePose(player, initial.Initial);
        _recording = new Track { Role = role, OffsetMs = initial.OffsetMs };
        _countdownAt = Server.TickCount + ReplayCoordinator.ToTicks(Config.CountdownSeconds * 1000, Server.TickInterval);
        _lastCountdown = -1; _state = "countdown";
        _hudNextTick = 0; _hudUntil = 0;
    }
    private void FinishRecording()
    {
        if (_api == null || _recording == null || _tactic == null) return;
        if (!_api.StopRecord(_ownerSlot)) throw new InvalidOperationException("原生录制停止失败。");
        var motion = _api.GetRecordedMotionExtended(_ownerSlot);
        if (motion.ticks.Length == 0) { _recording = null; _state = "idle"; Notify("录制为空，保留原轨迹。"); return; }
        _recording.Ticks = motion.ticks; _recording.Subticks = motion.subs; _recording.Commands = motion.commands;
        var candidate = new Tactic { Name = _tactic.Name, Map = _tactic.Map, Team = _tactic.Team,
            TickInterval = _tactic.TickInterval, Tracks = new() { [_recording.Role] = _recording } };
        candidate.Validate(MaxTicks);
        _tactic.Tracks[_recording.Role] = _recording;
        RecordingFinishedHud(_recording.Role, motion.ticks.Length);
        Notify($"队员 {_recording.Role} 已录制 {motion.ticks.Length} 帧，{motion.subs.Length} Subtick；其他队员未改。!ks save 保存。");
        _recording = null; _state = "idle";
    }
    private void BeginReplay(int[] roles)
    {
        if (_tactic == null || roles.Length == 0 || roles.Any(r => !_tactic.Tracks.ContainsKey(r))) throw new InvalidOperationException("指定队员没有录制。");
        if (roles.Length > Config.MaxRecordedPlayers) throw new InvalidOperationException("有效轨迹数超过配置的人数上限。");
        _tactic.Validate(MaxTicks);
        _lastReport.Clear(); _bots.Clear(); _readyAt = 0;
        _spawnBaseline.Clear();
        foreach (var p in Utilities.GetPlayers()) if (p.IsValid && p.UserId is int id) _spawnBaseline.Add(id);
        string prefix = "KS_" + Guid.NewGuid().ToString("N")[..8];
        foreach (int role in roles)
        {
            string name = prefix + "_" + role;
            _bots.Add((role, name, -1));
            Server.ExecuteCommand($"bot_add_{(_tactic.Team == 2 ? "t" : "ct")}");
        }
        // bot_add increases the engine quota; prevent replacement bots from accumulating between replays.
        Server.ExecuteCommand("bot_quota " + roles.Length);
        _prepareDeadline = Server.TickCount + ReplayCoordinator.ToTicks(15000, Server.TickInterval);
        _state = "preparing"; Notify("正在生成并预加载 BOT，全部就绪后统一开始。");
    }
    private void PrepareTick()
    {
        if (Server.TickCount > _prepareDeadline) throw new TimeoutException("BOT 创建/出生超时，已取消。");
        if (_readyAt > 0)
        {
            if (Server.TickCount < _readyAt) return;
            _replay!.Begin(Server.TickCount + 1, _bots.Select(b => new ReplayEntry(b.Role, b.Slot,
                ReplayCoordinator.ToTicks(_tactic!.Tracks[b.Role].OffsetMs, Server.TickInterval), _tactic!.Tracks[b.Role].Ticks.Length)).ToArray());
            _state = "playing"; return;
        }
        var players = Utilities.GetPlayers();
        for (int i = 0; i < _bots.Count; i++)
        {
            var binding = _bots[i];
            if (binding.Slot >= 0) continue;
            var bot = players.FirstOrDefault(p => p.IsValid && p.IsBot && p.UserId is int id && !_spawnBaseline.Contains(id) && p.TeamNum == _tactic!.Team);
            if (bot == null) continue;
            bot.PlayerName = binding.Name;
            Utilities.SetStateChanged(bot, "CBasePlayerController", "m_iszPlayerName");
            if (!bot.PawnIsAlive) { bot.Respawn(); continue; }
            _spawnBaseline.Add(bot.UserId!.Value);
            _bots[i] = (binding.Role, binding.Name, bot.Slot);
            _botIdentities[bot.Slot] = bot.Handle;
            _engine!.Prepare(bot, _tactic!.Tracks[binding.Role]);
        }
        if (_bots.All(b => b.Slot >= 0)) _readyAt = Server.TickCount + 2; // allow equipment networking to settle before one shared start
    }
    private void Tick()
    {
        _ticksSeen++;
        ReloadConfigurationIfChanged();
        while (_jobs.TryDequeue(out var job))
        { try { job(); } catch (Exception e) { Notify("完成回调失败: " + e.Message); } }
        try
        {
            RecordingHudTick();
            if (TrainingTick()) return;
            PracticeRespawnTick();
            VersusRespawnTick();
            VersusTick();
            XRayTick();
            if (DefenseProbeTick()) return;
            DefenseTick();
            DefenseSelfTestTick();
            DefenseCoopTestTick();
            VersusTestTick();
            if (SelfTestTick()) return;
            if (_state is "countdown" or "recording")
            {
                var player = NativeReplayEngine.Player(_ownerSlot);
                if (player == null || player.SteamID != _owner || !player.PawnIsAlive) throw new InvalidOperationException("录制玩家已失效。");
                if (_state == "countdown")
                {
                    int remaining = (int)Math.Ceiling((_countdownAt - Server.TickCount) * Server.TickInterval);
                    if (remaining > 0 && remaining != _lastCountdown) { _lastCountdown = remaining; Notify("录制倒计时 " + remaining); }
                    if (Server.TickCount >= _countdownAt)
                    {
                        int role = _recording!.Role, offset = _recording.OffsetMs;
                        _recording = Inventory.Capture(player, role); _recording.OffsetMs = offset;
                        if (!_api!.SetReplayPawn(player.Slot, player.PlayerPawn.Value!.Handle) || !_api.StartRecord(player.Slot))
                            throw new InvalidOperationException("原生录制接口失败，请查看 bc_status。");
                        _recordStart = Server.TickCount; _state = "recording"; Notify("开始录制队员 " + role);
                    }
                }
                else if (Server.TickCount - _recordStart >= MaxTicks) FinishRecording();
            }
            else if (_state == "preparing") PrepareTick();
            else if (_state == "playing")
            {
                _replay!.Tick(Server.TickCount);
                if (!_replay.Active)
                {
                    foreach (var e in _replay.Entries)
                        _lastReport.Add($"role={e.Role} startTick={e.StartedTick} requested={_replay.StartTick + e.OffsetTicks} maxCursorDrift={e.MaxCursorDrift} maxPositionError={e.MaxPositionError:F3} samples={e.PositionSamples}");
                    foreach (var line in _lastReport) Notify(line);
                    CleanupBots(); _state = "idle"; Notify("回放结束，可再次 !ks play。");
                }
            }
        }
        catch (Exception e)
        {
            _lastError = e.Message;
            bool test = _selfTestPhase != 0;
            if (test)
            {
                try { File.WriteAllText(Path.Combine(_dataRoot, "selftest-report.json"), JsonSerializer.Serialize(new
                    { Result = "FAIL_SERVER_AUTOMATION", Stage = _selfTestPhase, Error = e.Message, TimeUtc = DateTimeOffset.UtcNow })); }
                catch (IOException writeError) { Log("自测报告写入失败: " + writeError.Message); }
            }
            Notify("操作中断: " + e.Message); Abort(e.Message, false);
            if (test && !OtherHumans(0) && _trainingMode == "Practice") { _tactic = null; ExitTraining(); }
        }
    }
    private void CleanupBots()
    {
        if (_bots.Count > 0 && _practice.Active) Server.ExecuteCommand("bot_quota 0");
        // Include requested bots that connected before cancellation but have not spawned yet.
        for (int i = 0; i < _bots.Count; i++)
        {
            var binding = _bots[i];
            if (binding.Slot >= 0 || _tactic == null) continue;
            var bot = Utilities.GetPlayers().FirstOrDefault(p => p.IsValid && p.IsBot && p.UserId is int id && !_spawnBaseline.Contains(id) && p.TeamNum == _tactic.Team);
            if (bot == null) continue;
            _spawnBaseline.Add(bot.UserId!.Value); _botIdentities[bot.Slot] = bot.Handle;
            _bots[i] = (binding.Role, binding.Name, bot.Slot);
        }
        foreach (var b in _bots)
        {
            if (b.Slot >= 0) { try { _engine?.Stop(b.Slot); } catch (Exception e) { Log("释放 BOT: " + e.Message); } }
            // Names are generated by this plugin; do not kick other bots or reused player slots.
            KickOwnedBot(b.Name, b.Slot);
        }
        _bots.Clear();
    }
    private void KickOwnedBot(string name, int slot)
    {
        var bot = slot >= 0 ? NativeReplayEngine.Player(slot) : Utilities.GetPlayers().FirstOrDefault(p => p.IsValid && p.IsBot && p.PlayerName == name);
        bool owned = slot >= 0 ? _botIdentities.TryGetValue(slot, out var handle) && bot?.Handle == handle : bot?.PlayerName == name;
        if (owned && bot is { IsValid: true, IsBot: true } && bot.UserId is int userId)
            Server.ExecuteCommand("kickid " + userId);
        if (slot >= 0) _botIdentities.Remove(slot);
    }
    private void Abort(string reason, bool exitPractice)
    {
        EndVersusTest();
        XRayClear(); StopVersus(reason, close: true);
        EndDefenseSelfTest();
        EndDefenseCoopTest();
        CloseDefense(reason);
        StopDefenseProbe();
        try { ClearRecordingHud(); } catch (InvalidOperationException) { /* player disconnected */ }
        _hudUntil = 0; _hudNextTick = 0;
        _generation++; _busy = false;
        try { if (_state == "recording") _api?.StopRecord(_ownerSlot); } catch (Exception e) { Log("停止录制: " + e.Message); }
        _recording = null; _replay?.Cancel(); CleanupBots(); CancelSelfTest(); _state = "idle";
        if (exitPractice && _practice != null)
        { _practice.Exit(); _owner = 0; _ownerSlot = -1; }
        Log("安全停止: " + reason);
    }
}
