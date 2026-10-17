using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using KeranStrat.Core;

namespace KeranStrat.Plugin;

public sealed partial class KeranStratPlugin
{
    private HashSet<string> _defenseAllowed = new(StringComparer.Ordinal);
    private Tactic? _defenseTactic;
    private DefenseSettings? _defenseSettings;
    private int _defenseOwnerSlot = -1;
    private ulong _defenseOwner;
    private bool _defenseLoading;
    private int _defenseGeneration;
    private bool _modeDefense;
    private string _defenseError = "none";
    private DefenseStatistics _defenseStats = new();
    private bool DefenseBusy => _defenseLoading || _defenseState != "idle";
    private static readonly JsonSerializerOptions DefenseJson = new() { WriteIndented = true };
    private static void DefensePracticeFunds(CCSPlayerController player)
    {
        if (player.InGameMoneyServices == null) return;
        player.InGameMoneyServices.Account = 60000;
        Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInGameMoneyServices");
    }

    private void DefenseNotify(string text)
    {
        Server.PrintToConsole("[KeranStrat Defense] " + text);
        if (_defenseSession != null)
        {
            foreach (var member in _defenseSession.Members.Values)
                if (DefenseResolve(member) is { IsBot: false } p) p.PrintToChat("[KeranStrat Defense] " + text);
        }
        else
        {
            var player = NativeReplayEngine.Player(_defenseOwnerSlot);
            if (player is { IsValid: true } && player.SteamID == _defenseOwner) player.PrintToChat("[KeranStrat Defense] " + text);
        }
    }
    private void DefenseRequireIdle()
    {
        if (_modeChanging || _busy || _state != "idle" || _probePhase != 0 || _selfTestPhase != 0)
            throw new InvalidOperationException("当前已有原版录制、回放或切图操作进行中。");
    }
    private void DefenseRequireOwner(CCSPlayerController p)
    {
        if (_defenseSession != null && !_defenseSession.IsOwner(DefenseIdentityOf(p)) ||
            _defenseLoading && (_defenseOwnerSlot != p.Slot || _defenseOwner != p.SteamID))
            throw new InvalidOperationException("只有当前 Defense 房主可以执行此操作。");
    }
    private string DefenseSettingsPath() => Path.Combine(_dataRoot, "defense", _defenseOwner.ToString(), _defenseTactic!.Id.ToString("N") + ".json");
    private void DefenseSaveSettings()
    {
        if (_defenseTactic == null || _defenseSettings == null) return;
        if (DefenseDiagnostic) return;
        _defenseSettings.Validate(_defenseTactic);
        string path = DefenseSettingsPath(); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path) && Directory.EnumerateFiles(Path.Combine(_dataRoot, "defense"), "*.json", SearchOption.AllDirectories).Take(1024).Count() >= 1024)
            throw new IOException("Defense 场景配置已达到总数上限 1024。");
        string json = JsonSerializer.Serialize(_defenseSettings, DefenseJson);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 65536) throw new IOException("Defense 场景配置过大。");
        File.WriteAllText(path + ".tmp", json); File.Move(path + ".tmp", path, true);
    }
    private void DefenseLoad(CCSPlayerController p, string query)
    {
        string map = Server.MapName; double interval = Server.TickInterval; string gameVersion = _gameVersion; int generation = ++_defenseGeneration;
        _defenseOwner = p.SteamID; _defenseOwnerSlot = p.Slot; _defenseLoading = true;
        ulong owner = _defenseOwner;
        _ = Task.Run(() =>
        {
            try
            {
                var tactic = _store.Load(map, query);
                if (tactic.Team != 2 || tactic.Tracks.Count == 0) throw new InvalidDataException("Defense 只能加载有有效轨迹的 T 方战术。");
                if (tactic.GameVersion != gameVersion || Math.Abs(tactic.TickInterval - interval) > .000001)
                    throw new InvalidDataException("战术游戏版本或 Tick 间隔不兼容。");
                var settings = new DefenseSettings { TacticId = tactic.Id, Map = map, RoundSeconds = Config.DefenseRoundSeconds };
                string path = Path.Combine(_dataRoot, "defense", owner.ToString(), tactic.Id.ToString("N") + ".json");
                if (File.Exists(path))
                {
                    if (new FileInfo(path).Length > 65536) throw new InvalidDataException("Defense 场景配置过大。");
                    settings = JsonSerializer.Deserialize<DefenseSettings>(File.ReadAllText(path)) ?? throw new InvalidDataException("Defense 场景无效。");
                }
                settings.MigrateOwner(owner.ToString());
                settings.Validate(tactic);
                _jobs.Enqueue(() =>
                {
                    if (generation != _defenseGeneration) return;
                    if (!p.IsValid || p.SteamID != owner || p.TeamNum != 3 || !DefenseAuthorized(p))
                    { _defenseLoading = false; if (_defenseSession == null) { _defenseOwner = 0; _defenseOwnerSlot = -1; } return; }
                    _defenseLoading = false; _defenseTactic = tactic; _defenseSettings = settings;
                    _modeDefense = true;
                    if (_defenseSession == null) { _defenseSession = new DefenseSession(DefenseCreateParticipant(p)); _defenseStats = new(); }
                    DefenseNotify($"已加载 {tactic.Name}，T 队员：{string.Join(',', tactic.Tracks.Keys.Order())}。切换 CT，!ksd position 设置防守位置。");
                });
            }
            catch (Exception e) { _jobs.Enqueue(() => { if (generation != _defenseGeneration) return; _defenseLoading = false; DefenseNotify("加载失败：" + e.Message); if (_defenseSession == null) { _defenseOwner = 0; _defenseOwnerSlot = -1; } }); }
        });
    }
    private void DefenseCommand(CCSPlayerController? player, CommandInfo cmd)
    {
        void Reply(string s) => cmd.ReplyToCommand("[KeranStrat Defense] " + s);
        string? id = null;
        try { if (player is { IsValid: true, IsBot: false }) id = player.AuthorizedSteamID?.SteamId64.ToString(); } catch (InvalidOperationException) { }
        if (player != null && !AccessPolicy.Allows(Config.DefenseEnabled, _defenseAllowed, id))
        { Reply("你没有权限使用 Defense 模式。"); return; }
        string action = cmd.ArgCount > 1 ? cmd.GetArg(1).ToLowerInvariant() : "help";
        try
        {
            if (action == "help")
            { Reply("!ksd practice | exit | close | map <地图> | list | load <名称/ID> | join | leave | team | position | start | stop | status | repeat on/off | difficulty <1-4> | handoff <1-5> <毫秒/auto> | reload"); return; }
            if (action == "team") { DefenseTeam(Reply); return; }
            if (action == "status")
            {
                Reply($"mode={_trainingMode}, changing={_modeChanging}, state={_defenseState}, loading={_defenseLoading}, tactic={_defenseTactic?.Name ?? "none"}, difficulty={_defenseSettings?.Difficulty}, repeat={_defenseSettings?.Repeat}");
                Reply($"总计 {_defenseStats.Rounds} 轮；成功 {_defenseStats.Success}，失败 {_defenseStats.Failure}，超时 {_defenseStats.Timeouts}，平局 {_defenseStats.Draws}；error={_defenseError}");
                if (_defenseSession != null) Reply($"队伍 {_defenseSession.Members.Count}/5；本轮 CT 存活 {_defenseSession.Round.Values.Count(m => !m.Dead)}/{_defenseSession.Round.Count}；T BOT 剩余 {_defenseBots.Count - (_defenseState is "running" or "between" ? _defenseController?.DeadCount ?? 0 : 0)}/{_defenseBots.Count}。");
                if (player == null) Reply($"diagnostic probe={_probePhase}, coop={_coopTestPhase}, case={_coopCase}");
                if (_defenseController != null && _defenseState is "running" or "between") foreach (var e in _defenseController.Entries) Reply($"队员 {e.Role}: {e.State}, start={e.StartedTick}, handoff={e.HandoffTick}");
                return;
            }
            if (player == null)
            {
                if (action == "stop") { StopDefense("服务端停止"); Reply("已停止，保留 Practice。"); return; }
                if (action == "close") { CloseDefense("服务端解散"); Reply("已解散，保留 Practice。"); return; }
                if (action == "reload") { ReloadSettings(); Reply("Defense 配置已重载。"); return; }
                throw new InvalidOperationException("此命令需要 Defense 白名单玩家在游戏内使用。");
            }
            DefenseRequireIdle();
            if (action == "join")
            {
                if (_defenseSession == null) throw new InvalidOperationException("当前没有会话，请先由房主 !ksd load 战术。");
                if (DefenseBusy) throw new InvalidOperationException("请等房主停止训练后再加入。");
                if (player.TeamNum != 3) throw new InvalidOperationException("请先加入 CT 阵营。");
                bool added = _defenseSession.Join(DefenseCreateParticipant(player));
                Reply(added ? $"已加入防守队伍（{_defenseSession.Members.Count}/5）。" : "你已经加入防守队伍。"); return;
            }
            if (action == "leave")
            {
                DefenseRequireMember(player);
                var identity = DefenseIdentityOf(player);
                if (_defenseSession!.IsOwner(identity)) throw new InvalidOperationException("房主请用 !ksd stop 停止，或 !ksd close 解散会话。");
                if (_defenseState is "running" or "preparing" or "respawning" or "restoring")
                { _defenseSession.Members[identity.SteamId].PendingLeave = true; Reply("本轮结束后退出防守队伍。"); }
                else { _defenseSession.Remove(identity); if (DefenseBusy) player.ChangeTeam(CounterStrikeSharp.API.Modules.Utils.CsTeam.Spectator); Reply("已退出防守队伍。"); }
                return;
            }
            if (action == "position")
            {
                DefenseRequireMember(player);
                if (DefenseBusy) throw new InvalidOperationException("请等房主停止训练后再保存位置。");
                if (_defenseSettings == null || player.TeamNum != 3 || !player.PawnIsAlive) throw new InvalidOperationException("需要 CT 阵营并已出生。");
                string memberId = DefenseIdentityOf(player).SteamId;
                if (!_defenseSettings.Defenders.ContainsKey(memberId) && _defenseSettings.Defenders.Count >= 32) throw new IOException("本场景已保存 32 名玩家，请管理员清理历史位置。");
                var position = Inventory.Capture(player, 1); _defenseSettings.Defenders[memberId] = position;
                if (memberId == _defenseSession!.OwnerId) _defenseSettings.Defender = position;
                DefenseSaveSettings(); Reply("已保存你的 CT 防守位置、视角和装备。"); return;
            }
            if (action == "practice" && _trainingMode == "Practice")
            { DefensePracticeFunds(player); Reply("已进入训练环境，可按 B 购买防守装备。"); return; }
            if (action == "list")
            {
                string listMap = Server.MapName; int generation = _defenseGeneration;
                _ = Task.Run(() =>
                {
                    try { var list = _store.List(listMap).Where(t => t.Team == 2).ToArray(); _jobs.Enqueue(() => { if (generation == _defenseGeneration && player.IsValid) Reply(list.Length == 0 ? "当前地图没有 T 方战术。" : string.Join("；", list.Select(t => t.Name + " [" + t.Id.ToString("N") + "]"))); }); }
                    catch (Exception e) { _jobs.Enqueue(() => { if (player.IsValid) Reply(e.Message); }); }
                }); Reply("正在读取 T 方战术列表。"); return;
            }
            DefenseRequireOwner(player);
            if (action == "stop") { StopDefense("主动停止"); Reply("已停止，保留 Practice。"); return; }
            if (action == "close") { CloseDefense("房主解散"); Reply("已解散，保留 Practice。"); return; }
            if (action == "repeat")
            {
                if (_defenseSettings == null || cmd.GetArg(2) is not ("on" or "off")) throw new ArgumentException("请先加载战术；用法 !ksd repeat on/off。");
                _defenseSettings.Repeat = cmd.GetArg(2) == "on"; DefenseSaveSettings(); Reply("自动循环 " + cmd.GetArg(2)); return;
            }
            if (action is "exit" or "map")
            {
                CloseDefense("切换地图或退出"); _defenseOwner = player.SteamID; _defenseOwnerSlot = player.Slot;
                _modeDefense = true;
                if (action == "exit") ExitTraining(); else ChangeTrainingMap(cmd.GetArg(2));
                Reply("已请求切换，等待地图和规则就绪。"); return;
            }
            if (DefenseBusy) throw new InvalidOperationException("请先 !ksd stop 停止当前训练或加载。");
            if (action == "practice")
            {
                if (_trainingMode == "Practice") { _modeDefense = true; DefensePracticeFunds(player); Reply("已进入训练环境，可按 B 购买防守装备。"); return; }
                _modeDefense = true; EnterTraining(); Reply("正在进入训练环境。"); return;
            }
            if (action == "reload") { ReloadSettings(); Reply("Defense 配置已重新加载。"); return; }
            if (_trainingMode != "Practice") throw new InvalidOperationException("请先 !ksd practice。");
            switch (action)
            {
                case "load":
                    if (player.TeamNum != 3) throw new InvalidOperationException("房主请先加入 CT 阵营。");
                    DefenseLoad(player, Args(cmd, 2)); Reply("正在读取战术。"); break;
                case "start": StartDefense(player); Reply("Defense 训练已启动。"); break;
                case "difficulty":
                    if (_defenseSettings == null || !int.TryParse(cmd.GetArg(2), out int difficulty) || difficulty is < 1 or > 4) throw new ArgumentException("先加载战术；难度为 1～4。");
                    _defenseSettings.Difficulty = difficulty; DefenseSaveSettings(); Reply("BOT 难度已设置为 " + difficulty); break;
                case "handoff":
                    if (_defenseTactic == null || _defenseSettings == null || !int.TryParse(cmd.GetArg(2), out int role) || !_defenseTactic.Tracks.TryGetValue(role, out var track)) throw new ArgumentException("指定队员没有轨迹。");
                    int? ms = null;
                    if (cmd.GetArg(3) != "auto") { if (!int.TryParse(cmd.GetArg(3), out int parsed)) throw new ArgumentException("交接时间为毫秒或 auto。"); ms = parsed; }
                    DefenseBotController.HandoffTicks(ms, track, _defenseTactic.TickInterval);
                    _defenseSettings.HandoffMilliseconds[role] = ms; DefenseSaveSettings(); Reply($"队员 {role} 交接：{ms?.ToString() ?? "auto"}（从自身轨迹开始计算）。"); break;
                default: throw new ArgumentException("未知 Defense 指令，!ksd 查看帮助。");
            }
        }
        catch (Exception e) { _defenseError = e.Message; Reply(e.Message); }
    }
}
